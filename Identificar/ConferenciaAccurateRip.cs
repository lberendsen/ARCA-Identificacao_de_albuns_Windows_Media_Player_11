using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using ARCA.Central;
using ARCA.Interop;
using ARCA.Modelo;

namespace ARCA.Identificar
{
    /// <summary>Um CD de um álbum: as faixas que guardam o mesmo índice, na ordem do disco, e o que a conferência deu.</summary>
    public sealed class TDiscoConferido
    {
        public string strMcdi;
        public readonly List<string> lstArquivos = new List<string>();
        public TTocAudio objToc;
        public TResultadoAccurateRip objResultado;
        /// <summary>Por que não deu para conferir, ou null.</summary>
        public string strProblema;
        /// <summary>Algo a dizer que não impediu a conferência, ou null.</summary>
        public string strObservacao;
    }

    /// <summary>
    /// Conferência no AccurateRip de um álbum que já está no acervo, sem o disco
    /// (a JanelaConferenciaAlbum).
    ///
    /// Os WMA do Lorenzo são WMA Lossless (1152 kbps, todos na amostra de
    /// 20/11/2026) e guardam o índice do CD (WM/MCDI): com ele as faixas são
    /// remontadas como o disco e as assinaturas v1/v2 de cada uma comparadas com
    /// as de quem ripou o mesmo CD. É a única forma de saber, anos depois, se a
    /// faixa é bit-perfect.
    ///
    /// O Vista não sai para a internet, então o arquivo
    /// da base (dBAR-….bin) vem pelo ARCA Central (/accuraterip). O cálculo, que
    /// precisa do áudio, é aqui — com o código de Comum\ (AccurateRip.Calcular,
    /// ProcurarOffsets, TFonteWma). Só lê; gravar o veredito é à parte.
    /// </summary>
    public static class ConferenciaAccurateRip
    {
        /// <summary>As faixas WMA agrupadas pelo índice do CD (um grupo por disco), cada grupo na ordem do número da faixa.</summary>
        public static List<TDiscoConferido> Agrupar(IEnumerable<string> lstArquivos, out int intSemIndice)
        {
            intSemIndice = 0;
            Dictionary<string, TDiscoConferido> dic = new Dictionary<string, TDiscoConferido>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> dicNumero = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<TDiscoConferido> lst = new List<TDiscoConferido>();
            foreach (string f in lstArquivos.Where(TFaixaMp3.EhWma))
            {
                string strMcdi = TFaixaMp3.IndiceDoCd(f);
                if (strMcdi == null) { intSemIndice++; continue; }
                try { dicNumero[f] = TFaixaMp3.Ler(f).intFaixaTag; } catch { dicNumero[f] = 0; }
                TDiscoConferido d;
                if (!dic.TryGetValue(strMcdi, out d)) { dic[strMcdi] = d = new TDiscoConferido { strMcdi = strMcdi }; lst.Add(d); }
                d.lstArquivos.Add(f);
            }
            foreach (TDiscoConferido d in lst)
            {
                // A ordem do disco vem do número da faixa, não do nome do arquivo. Sem número
                // em todas (ou com repetição), fica a do nome — e a pessoa é avisada.
                List<int> lstN = d.lstArquivos.Select(f => dicNumero[f]).ToList();
                bool blnNumeracaoOk = lstN.All(n => n > 0) && lstN.Distinct().Count() == lstN.Count;
                List<string> lstOrdem = blnNumeracaoOk ? d.lstArquivos.OrderBy(f => dicNumero[f]).ToList()
                                                       : d.lstArquivos.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                d.lstArquivos.Clear();
                d.lstArquivos.AddRange(lstOrdem);
                if (!blnNumeracaoOk) d.strObservacao = "o número da faixa falta ou se repete; a ordem usada foi a dos nomes dos arquivos";
            }
            return lst;
        }

        /// <summary>Confere um disco. Preenche objResultado, ou strProblema.</summary>
        public static void Conferir(TDiscoConferido d, Action<string> actEstado, Func<bool> cancelou)
        {
            d.objToc = TTocAudio.DeMcdi(d.strMcdi, d.lstArquivos.Count);
            if (d.objToc == null) { d.strProblema = "o índice do CD gravado nas faixas não é legível"; return; }

            actEstado("decodificando as faixas para conferir o tamanho de cada uma contra o índice do CD…");
            using (TFonteWma objFonte = new TFonteWma(d.lstArquivos, d.objToc.lstInicios[0]))
            {
                string strInvalido = objFonte.Validar(d.objToc, cancelou);
                if (strInvalido != null) { d.strProblema = strInvalido; return; }
                if (!string.IsNullOrEmpty(objFonte.Observacao))
                    d.strObservacao = string.IsNullOrEmpty(d.strObservacao) ? objFonte.Observacao : objFonte.Observacao + "; " + d.strObservacao;

                actEstado("consultando o AccurateRip pelo ARCA Central…");
                TResposta r = ClienteCentral.AccurateRip(d.strMcdi, d.lstArquivos.Count);
                if (!string.IsNullOrEmpty(r.strErro)) { d.strProblema = r.strErro; return; }
                string strFalha;
                List<AccurateRip.TPrensagem> lstBase = r.blnForaDaBase ? new List<AccurateRip.TPrensagem>()
                                                     : Interpretar(Convert.FromBase64String(r.strDadosBase64 ?? ""), d.objToc, out strFalha);
                TResultadoAccurateRip res = new TResultadoAccurateRip { strUrl = r.strUrl, blnNaBase = lstBase.Count > 0 };

                actEstado("calculando as assinaturas das faixas…");
                List<int> lstPrimeira = new List<int> { 0 };
                foreach (int k in OffsetDrive.OffsetsDoAcervo()) if (!lstPrimeira.Contains(k)) lstPrimeira.Add(k);
                uint[,] v1, v2;
                int[] arrK = lstPrimeira.ToArray();
                AccurateRip.Calcular(objFonte, d.objToc, arrK, out v1, out v2, cancelou);
                res = Melhor(res, d.objToc, lstBase, v1, v2, arrK, r.strUrl);
                if (res.blnNaBase && !res.TodasConferem)
                {
                    actEstado("procurando o deslocamento do áudio…");
                    List<int> lstK = AccurateRip.ProcurarOffsets(objFonte, d.objToc, lstBase, cancelou);
                    lstK.RemoveAll(lstPrimeira.Contains);
                    if (lstK.Count > 0)
                    {
                        arrK = lstK.GetRange(0, Math.Min(3, lstK.Count)).ToArray();
                        AccurateRip.Calcular(objFonte, d.objToc, arrK, out v1, out v2, cancelou);
                        res = Melhor(res, d.objToc, lstBase, v1, v2, arrK, r.strUrl);
                    }
                }
                d.objResultado = res;
                if (res.QtdConferem > 0) OffsetDrive.RegistrarOffsetDoAcervo(res.intOffset);
            }
        }

        /// <summary>As prensagens do arquivo dBAR (o formato que o AccurateRip.Interpretar lê).</summary>
        public static List<AccurateRip.TPrensagem> Interpretar(byte[] arr, TTocAudio objToc, out string strFalha)
        {
            strFalha = null;
            uint id1, id2, idF;
            AccurateRip.CalcularIds(objToc, out id1, out id2, out idF);
            List<AccurateRip.TPrensagem> lst = new List<AccurateRip.TPrensagem>();
            int p = 0;
            while (p + 13 <= arr.Length)
            {
                int n = arr[p];
                uint c1 = BitConverter.ToUInt32(arr, p + 1), c2 = BitConverter.ToUInt32(arr, p + 5), c3 = BitConverter.ToUInt32(arr, p + 9);
                p += 13;
                if (p + n * 9 > arr.Length) break;
                bool blnEste = n == objToc.QtdFaixas && c1 == id1 && c2 == id2 && c3 == idF;
                AccurateRip.TPrensagem objP = new AccurateRip.TPrensagem { arrConfianca = new int[n], arrCrc = new uint[n] };
                for (int i = 0; i < n; i++) { objP.arrConfianca[i] = arr[p]; objP.arrCrc[i] = BitConverter.ToUInt32(arr, p + 1); p += 9; }
                if (blnEste) lst.Add(objP);
            }
            if (lst.Count == 0 && arr.Length > 0) strFalha = "a resposta do AccurateRip não é deste disco";
            return lst;
        }

        private static TResultadoAccurateRip Melhor(TResultadoAccurateRip rAtual, TTocAudio objToc, List<AccurateRip.TPrensagem> lstBase,
                                                    uint[,] v1, uint[,] v2, int[] arrK, string strUrl)
        {
            TResultadoAccurateRip r = rAtual;
            for (int o = 0; o < arrK.Length; o++)
            {
                TResultadoAccurateRip rk = new TResultadoAccurateRip { blnNaBase = lstBase.Count > 0, strUrl = strUrl, intOffset = arrK[o] };
                for (int t = 0; t < objToc.QtdFaixas; t++)
                {
                    TFaixaAccurateRip f = new TFaixaAccurateRip { intNumero = objToc.intPrimeiraFaixa + t, uintV1 = v1[o, t], uintV2 = v2[o, t] };
                    foreach (AccurateRip.TPrensagem objP in lstBase)
                    {
                        f.intTotalNaBase += objP.arrConfianca[t];
                        if (objP.arrCrc[t] == f.uintV2) { f.intConfianca += objP.arrConfianca[t]; f.strVersao = "v2"; }
                        else if (objP.arrCrc[t] == f.uintV1) { f.intConfianca += objP.arrConfianca[t]; if (f.strVersao == null) f.strVersao = "v1"; }
                    }
                    rk.lstFaixas.Add(f);
                }
                if (r.lstFaixas.Count == 0 || rk.QtdConferem > r.QtdConferem) r = rk;
            }
            return r;
        }

        /// <summary>O veredito de uma faixa, gravado no atributo ARCA/Verification.</summary>
        public static string Veredito(TResultadoAccurateRip r, TFaixaAccurateRip f, DateTime dt)
        {
            return f.Confere
                ? string.Format("BIT-PERFECT COMPROVADO — AccurateRip {0} (confiança {1}), conferido em {2:dd/MM/yyyy}{3}", f.strVersao, f.intConfianca, dt,
                                r.intOffset != 0 ? string.Format("; áudio deslocado {0:+0;-0} amostras", r.intOffset) : "")
                : string.Format("NÃO comprovado — não confere com o AccurateRip ({0} registro(s)), conferido em {1:dd/MM/yyyy}", f.intTotalNaBase, dt);
        }

        /// <summary>Grava o veredito de cada faixa (atributo ARCA/Verification; o áudio não é tocado). Devolve as que não deu.</summary>
        public static List<string> GravarVeredito(TDiscoConferido d)
        {
            List<string> lstFalhas = new List<string>();
            TResultadoAccurateRip r = d.objResultado;
            if (r == null || !r.blnNaBase) return lstFalhas;
            DateTime dt = DateTime.Now;
            for (int i = 0; i < r.lstFaixas.Count && i < d.lstArquivos.Count; i++)
            {
                try
                {
                    using (EditorAsf e = new EditorAsf())
                        if (!(e.Abrir(d.lstArquivos[i]) && e.DefinirTexto("ARCA/Verification", Veredito(r, r.lstFaixas[i], dt)) && e.Salvar()))
                            lstFalhas.Add(Path.GetFileName(d.lstArquivos[i]));
                }
                catch { lstFalhas.Add(Path.GetFileName(d.lstArquivos[i])); }
            }
            return lstFalhas;
        }

        /// <summary>A conferência no ledger do acervo (procedência), uma linha por disco.</summary>
        public static void Registrar(TDiscoConferido d, string strPasta)
        {
            if (d.objResultado == null) return;
            TResultadoAccurateRip r = d.objResultado;
            Dictionary<string, object> obj = new Dictionary<string, object>();
            obj["quando"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            obj["evento"] = "conferência AccurateRip (ARCA Identificar)";
            obj["destino"] = strPasta;
            obj["indiceDoCd"] = d.strMcdi;
            obj["naBase"] = r.blnNaBase;
            obj["conferem"] = r.QtdConferem;
            obj["faixas"] = r.lstFaixas.Count;
            obj["deslocamento"] = r.intOffset;
            obj["arquivos"] = d.lstArquivos.Select(Path.GetFileName).ToList();
            File.AppendAllText(GravadorMp3.CaminhoDoLedger(), new JavaScriptSerializer().Serialize(obj) + "\n", new UTF8Encoding(false));
        }
    }
}
