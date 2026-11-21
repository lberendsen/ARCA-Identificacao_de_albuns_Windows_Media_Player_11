using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace ARCA.Modelo
{
    /// <summary>
    /// Índice de áudio de um CD: onde começa cada faixa de áudio (LBA, a
    /// partir de 0) e onde o áudio acaba. É o que identifica o disco no
    /// AccurateRip e diz onde cortar cada faixa.
    /// </summary>
    public sealed class TTocAudio
    {
        public const int AmostrasPorSetor = 588;          // 2352 bytes / 4 (16 bits, estéreo)
        public const int GapSessaoCdExtra = 11400;        // lead-out + lead-in entre as sessões

        public readonly List<int> lstInicios = new List<int>();
        public int intLeadOut;                            // fim do áudio (LBA)
        public int intPrimeiraFaixa = 1;                  // número da primeira faixa
        /// <summary>Início da faixa de dados (CD Extra), ou -1. Entra só no ID do freedb.</summary>
        public int intInicioDados = -1;
        /// <summary>Lead-out físico do disco, quando há sessão de dados depois do áudio.</summary>
        public int intLeadOutFisico = -1;
        /// <summary>Início da faixa de dados que vem ANTES do áudio (CD mixed-mode), ou -1.</summary>
        public int intDadosAntes = -1;

        public int QtdFaixas { get { return lstInicios.Count; } }

        public int FimDaFaixa(int i) { return i + 1 < lstInicios.Count ? lstInicios[i + 1] : intLeadOut; }

        public long AmostrasDaFaixa(int i) { return (long)(FimDaFaixa(i) - lstInicios[i]) * AmostrasPorSetor; }

        /// <summary>
        /// O WM/MCDI que o Windows Media Player grava ao ripar: texto como
        /// "A+96+5C1C+…+2E55" — a quantidade de faixas e, em hexadecimal, o
        /// endereço de cada uma e o do lead-out, contando os 150 setores do
        /// pré-gap. intFaixasDeAudio diz quantas são áudio (CD Extra tem uma
        /// faixa de dados a mais no fim); 0 = todas.
        /// </summary>
        public static TTocAudio DeMcdi(string strMcdi, int intFaixasDeAudio)
        {
            if (string.IsNullOrEmpty(strMcdi)) return null;
            string[] arr = strMcdi.Trim().TrimEnd('\0').Split('+');
            int n;
            if (arr.Length < 3 || !int.TryParse(arr[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n)) return null;
            if (arr.Length < n + 2) return null;
            int[] arrEnd = new int[n + 1];
            for (int i = 0; i <= n; i++)
                if (!int.TryParse(arr[i + 1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out arrEnd[i])) return null;

            TTocAudio objToc = new TTocAudio();
            int intAudio = intFaixasDeAudio > 0 && intFaixasDeAudio < n ? intFaixasDeAudio : n;
            for (int i = 0; i < intAudio; i++) objToc.lstInicios.Add(arrEnd[i] - 150);
            if (intAudio < n)
            {
                objToc.intInicioDados = arrEnd[intAudio] - 150;
                objToc.intLeadOutFisico = arrEnd[n] - 150;
                objToc.intLeadOut = objToc.intInicioDados - GapSessaoCdExtra;
            }
            else objToc.intLeadOut = arrEnd[n] - 150;
            return objToc;
        }

        /// <summary>
        /// O índice a partir do AudioCD.cue da extração: INDEX 01 de cada
        /// faixa AUDIO (as posições do CUE contam do LBA 0 do disco), o
        /// REM LEAD-OUT da sessão de áudio e, em CD Extra, a faixa de dados e o
        /// REM LEAD-OUT-SESSION2. Sem lead-out no CUE, o tamanho do .bin.
        /// </summary>
        public static TTocAudio DeCue(string strCue, long lngBytesBin)
        {
            if (!File.Exists(strCue)) return null;
            TTocAudio objToc = new TTocAudio();
            string strTipo = null;
            int intNumero = 0, intLeadOut1 = -1;
            bool blnPrimeira = true;
            List<int> lstDados = new List<int>();
            bool blnExato = false;
            foreach (string strLinha in File.ReadAllLines(strCue))
            {
                string t = strLinha.Trim();
                string[] arr = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (arr.Length >= 3 && arr[0].Equals("TRACK", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(arr[1], out intNumero);
                    strTipo = arr[2].ToUpperInvariant();
                }
                else if (arr.Length >= 3 && arr[0].Equals("INDEX", StringComparison.OrdinalIgnoreCase) && arr[1] == "01")
                {
                    int intLba = Msf(arr[2]);
                    if (intLba < 0) continue;
                    if (strTipo == "AUDIO")
                    {
                        if (blnPrimeira) { objToc.intPrimeiraFaixa = intNumero; blnPrimeira = false; }
                        objToc.lstInicios.Add(intLba);
                    }
                    // Dados antes de qualquer áudio: CD mixed-mode (faixa 1 de dados).
                    // Não é sessão 2 e não mexe no fim do áudio.
                    else if (objToc.lstInicios.Count == 0) objToc.intDadosAntes = intLba;
                    else lstDados.Add(intLba);
                }
                else if (arr.Length >= 3 && arr[0] == "REM" && arr[1].Equals("LEAD-OUT", StringComparison.OrdinalIgnoreCase))
                    intLeadOut1 = Msf(arr[2]);
                else if (arr.Length >= 3 && arr[0] == "REM" && arr[1].Equals("LEAD-OUT-SESSION2", StringComparison.OrdinalIgnoreCase))
                    objToc.intLeadOutFisico = Msf(arr[2]);
                else if (arr.Length >= 2 && arr[0] == "REM" && arr[1].Equals("ARCA-LEADOUT-EXATO", StringComparison.OrdinalIgnoreCase))
                    blnExato = true;
            }
            if (objToc.lstInicios.Count == 0) return null;
            // Mesma janela do extrator: até 11.600 setores (faixa real começa a 11.700 ou mais).
            if (lstDados.Count >= 2 && lstDados[1] - lstDados[0] >= 11000 && lstDados[1] - lstDados[0] <= 11600)
            {
                // Proteção CDS-300: a "faixa fantasma" fica no lead-out da
                // sessão 1 — é ali que o áudio acaba; os dados vêm depois.
                objToc.intLeadOut = lstDados[0];
                objToc.intInicioDados = lstDados[1];
            }
            else if (lstDados.Count >= 1)
            {
                // CD Extra: o áudio acaba no lead-out da sessão 1, que fica
                // 11.400 setores antes da faixa de dados — não no início dela.
                objToc.intInicioDados = lstDados[0];
                objToc.intLeadOut = objToc.intInicioDados - GapSessaoCdExtra;
            }
            else
                objToc.intLeadOut = intLeadOut1 > 0 ? intLeadOut1 : (int)(lngBytesBin / 2352);
            // O drive disse onde a sessão de áudio acaba (índice completo):
            // vale mais que qualquer estimativa acima.
            if (blnExato && intLeadOut1 > 0 && intLeadOut1 > objToc.lstInicios[objToc.lstInicios.Count - 1])
                objToc.intLeadOut = intLeadOut1;
            if (objToc.intInicioDados >= 0 && objToc.intLeadOutFisico < 0) objToc.intLeadOutFisico = (int)(lngBytesBin / 2352);
            return objToc;
        }

        private static int Msf(string s)
        {
            string[] p = s.Split(':');
            int m, sg, f;
            if (p.Length != 3 || !int.TryParse(p[0], out m) || !int.TryParse(p[1], out sg) || !int.TryParse(p[2], out f)) return -1;
            return (m * 60 + sg) * 75 + f;
        }

        /// <summary>
        /// O índice no formato WM/MCDI do Windows Media Player
        /// ("A+96+5C1C+…"), para gravar nas faixas: é o que permite conferir
        /// o álbum no AccurateRip anos depois, sem o disco.
        /// </summary>
        public string ParaMcdi()
        {
            List<int> lst = new List<int>(lstInicios);
            if (intDadosAntes >= 0) lst.Insert(0, intDadosAntes);   // mixed-mode: a faixa 1 é de dados
            int intFim = intLeadOut;
            if (intInicioDados >= 0) { lst.Add(intInicioDados); intFim = intLeadOutFisico; }
            StringBuilder sb = new StringBuilder();
            sb.Append(lst.Count.ToString("X", CultureInfo.InvariantCulture));
            foreach (int intLba in lst) sb.Append('+').Append((intLba + 150).ToString("X", CultureInfo.InvariantCulture));
            sb.Append('+').Append((intFim + 150).ToString("X", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    /// <summary>
    /// Uma faixa conferida: as duas assinaturas calculadas e o que a base
    /// disse delas.
    /// </summary>
    public sealed class TFaixaAccurateRip
    {
        public int intNumero;
        public uint uintV1, uintV2;
        /// <summary>Quantas pessoas riparam a faixa com exatamente estes números (soma das prensagens que conferem). 0 = não confere.</summary>
        public int intConfianca;
        /// <summary>Quantas submissões a base tem para esta faixa, conferindo ou não.</summary>
        public int intTotalNaBase;
        public string strVersao;   // "v2", "v1" ou null

        public bool Confere { get { return intConfianca > 0; } }
    }

    public sealed class TResultadoAccurateRip
    {
        /// <summary>O disco existe na base?</summary>
        public bool blnNaBase;
        /// <summary>Deslocamento, em amostras, em que as faixas foram conferidas (0 = como estão).</summary>
        public int intOffset;
        public string strUrl;
        public string strFalha;
        public readonly List<TFaixaAccurateRip> lstFaixas = new List<TFaixaAccurateRip>();

        public int QtdConferem { get { return lstFaixas.FindAll(f => f.Confere).Count; } }
        public bool TodasConferem { get { return lstFaixas.Count > 0 && QtdConferem == lstFaixas.Count; } }
    }

    /// <summary>
    /// Fonte de amostras de um disco inteiro, em sequência, como uint por
    /// amostra estéreo (esquerdo nos 16 bits baixos, direito nos altos — a
    /// ordem em que os bytes vêm do CD). A posição 0 é o LBA indicado por
    /// LbaInicial.
    /// </summary>
    public interface IFonteDisco
    {
        int LbaInicial { get; }
        long TotalAmostras { get; }
        void Reiniciar();
        /// <summary>Lê até intMax amostras; 0 no fim. Lança em erro.</summary>
        int Ler(uint[] arrDestino, int intMax);
    }

    /// <summary>
    /// AccurateRip: a base pública em que milhares de pessoas registraram a
    /// assinatura de cada faixa que riparam. Se a sua confere, a leitura do
    /// disco está certa — é a prova que nenhuma checagem local dá, porque um
    /// drive pode entregar um trecho estimado sem acusar erro.
    ///
    /// As duas assinaturas (v1 e v2) são somas das amostras ponderadas pela
    /// posição na faixa; as 5 primeiras setores da primeira faixa e os 5
    /// últimos da última ficam de fora (drives diferem nas pontas do disco).
    ///
    /// O deslocamento (offset) do drive desloca todas as amostras por um
    /// número fixo; a mesma música lida por dois drives sai "igual, mas
    /// deslocada". Aqui ele é achado em vez de exigido: a v1 de uma faixa do
    /// meio é recalculada para cada deslocamento possível em tempo constante
    /// por passo, e onde bate com a base, as outras faixas são conferidas.
    /// </summary>
    public static class AccurateRip
    {
        public const int AmostrasPontas = 5 * TTocAudio.AmostrasPorSetor;   // 2940
        /// <summary>Até onde procurar o deslocamento, em amostras para cada lado (~0,14 s).</summary>
        public const int AlcanceBusca = 6000;

        // ===============================================================
        //  Identificação do disco e consulta à base
        // ===============================================================
        public static void CalcularIds(TTocAudio objToc, out uint id1, out uint id2, out uint idFreedb)
        {
            id1 = 0; id2 = 0;
            int n = objToc.QtdFaixas;
            for (int i = 0; i < n; i++)
            {
                uint uintLba = (uint)objToc.lstInicios[i];
                id1 += uintLba;
                id2 += (uintLba > 0 ? uintLba : 1) * (uint)(i + 1);
            }
            id1 += (uint)objToc.intLeadOut;
            id2 += (uint)objToc.intLeadOut * (uint)(n + 1);

            // freedb: conta também a faixa de dados do CD Extra e o lead-out físico.
            List<int> lstTodas = new List<int>(objToc.lstInicios);
            if (objToc.intDadosAntes >= 0) lstTodas.Insert(0, objToc.intDadosAntes);
            int intFim = objToc.intLeadOut;
            if (objToc.intInicioDados >= 0) { lstTodas.Add(objToc.intInicioDados); intFim = objToc.intLeadOutFisico; }
            int intSoma = 0;
            foreach (int intLba in lstTodas)
            {
                int intSeg = (intLba + 150) / 75;
                while (intSeg > 0) { intSoma += intSeg % 10; intSeg /= 10; }
            }
            int intDuracao = (intFim + 150) / 75 - (lstTodas[0] + 150) / 75;
            idFreedb = ((uint)(intSoma % 255) << 24) | ((uint)intDuracao << 8) | (uint)lstTodas.Count;
        }

        public static string Url(TTocAudio objToc)
        {
            uint id1, id2, idF;
            CalcularIds(objToc, out id1, out id2, out idF);
            return string.Format(CultureInfo.InvariantCulture,
                "http://www.accuraterip.com/accuraterip/{0:x}/{1:x}/{2:x}/dBAR-{3:000}-{4:x8}-{5:x8}-{6:x8}.bin",
                id1 & 0xF, (id1 >> 4) & 0xF, (id1 >> 8) & 0xF, objToc.QtdFaixas, id1, id2, idF);
        }

        /// <summary>Uma prensagem registrada: confiança e assinatura de cada faixa.</summary>
        public sealed class TPrensagem
        {
            public int[] arrConfianca;
            public uint[] arrCrc;
        }

        /// <summary>
        /// As prensagens que a base tem para o disco. Lista vazia = disco não
        /// está na base (não é erro). null = não consegui consultar (strFalha).
        /// </summary>
        public static List<TPrensagem> Consultar(TTocAudio objToc, out string strUrl, out string strFalha)
        {
            strFalha = null;
            strUrl = Url(objToc);
            byte[] arrDados;
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(strUrl);
                req.Proxy = NetworkConfiguration.CreateProxy();
                req.UserAgent = "ARCA";
                req.Timeout = 20000;
                using (HttpWebResponse resp = NetworkConfiguration.Responder(req))
                using (Stream objFluxo = resp.GetResponseStream())
                using (MemoryStream objMem = new MemoryStream())
                {
                    objFluxo.CopyTo(objMem);
                    arrDados = objMem.ToArray();
                }
            }
            catch (WebException ex)
            {
                if (NetworkConfiguration.StatusHttp(ex) == 404) return new List<TPrensagem>();   // disco fora da base
                strFalha = "não consegui consultar o AccurateRip: " + ex.Message;
                return null;
            }
            catch (Exception ex)
            {
                strFalha = "não consegui consultar o AccurateRip: " + ex.Message;
                return null;
            }
            return Interpretar(arrDados, objToc, out strFalha);
        }

        private static List<TPrensagem> Interpretar(byte[] arr, TTocAudio objToc, out string strFalha)
        {
            strFalha = null;
            uint id1, id2, idF;
            CalcularIds(objToc, out id1, out id2, out idF);
            List<TPrensagem> lst = new List<TPrensagem>();
            int p = 0;
            while (p + 13 <= arr.Length)
            {
                int n = arr[p];
                uint c1 = BitConverter.ToUInt32(arr, p + 1), c2 = BitConverter.ToUInt32(arr, p + 5), c3 = BitConverter.ToUInt32(arr, p + 9);
                p += 13;
                if (p + n * 9 > arr.Length) break;
                // A resposta é do disco pedido; um bloco de outro disco não vale.
                bool blnEste = n == objToc.QtdFaixas && c1 == id1 && c2 == id2 && c3 == idF;
                TPrensagem objP = new TPrensagem { arrConfianca = new int[n], arrCrc = new uint[n] };
                for (int i = 0; i < n; i++)
                {
                    objP.arrConfianca[i] = arr[p];
                    objP.arrCrc[i] = BitConverter.ToUInt32(arr, p + 1);
                    p += 9;
                }
                if (blnEste) lst.Add(objP);
            }
            if (lst.Count == 0 && arr.Length > 0) strFalha = "a resposta do AccurateRip não é deste disco";
            return lst;
        }

        // ===============================================================
        //  Assinaturas
        // ===============================================================

        /// <summary>
        /// v1 e v2 de todas as faixas, para cada deslocamento pedido, numa
        /// passada só pela fonte. A amostra i da faixa t, no deslocamento k,
        /// é a da posição (início de t) + k + i do disco; fora do disco, zero.
        /// </summary>
        public static void Calcular(IFonteDisco objFonte, TTocAudio objToc, int[] arrOffsets,
                                    out uint[,] arrV1, out uint[,] arrV2, Func<bool> cancelou)
        {
            int n = objToc.QtdFaixas, m = arrOffsets.Length;
            arrV1 = new uint[m, n];
            arrV2 = new uint[m, n];
            long[] arrIni = new long[n], arrLen = new long[n], arrDe = new long[n], arrAte = new long[n];
            for (int t = 0; t < n; t++)
            {
                arrIni[t] = (long)(objToc.lstInicios[t] - objFonte.LbaInicial) * TTocAudio.AmostrasPorSetor;
                arrLen[t] = objToc.AmostrasDaFaixa(t);
                arrDe[t] = t == 0 ? AmostrasPontas : 1;
                arrAte[t] = t == n - 1 ? arrLen[t] - AmostrasPontas : arrLen[t];
            }
            int[] arrFaixaAtual = new int[m];

            objFonte.Reiniciar();
            uint[] buf = new uint[65536];
            long pos = 0;
            int lidos;
            while ((lidos = objFonte.Ler(buf, buf.Length)) > 0)
            {
                for (int o = 0; o < m; o++)
                {
                    int k = arrOffsets[o];
                    int t = arrFaixaAtual[o];
                    for (int j = 0; j < lidos; j++)
                    {
                        long q = pos + j - k;                       // posição no disco "corrigido"
                        while (t < n && q >= arrIni[t] + arrLen[t]) t++;
                        if (t >= n) break;
                        if (q < arrIni[t]) continue;
                        long mul = q - arrIni[t] + 1;
                        if (mul < arrDe[t] || mul > arrAte[t]) continue;
                        uint s = buf[j];
                        arrV1[o, t] += s * (uint)mul;
                        ulong prod = (ulong)s * (ulong)(uint)mul;
                        arrV2[o, t] += (uint)prod + (uint)(prod >> 32);
                    }
                    arrFaixaAtual[o] = t;
                }
                pos += lidos;
                if (cancelou != null && cancelou()) throw new OperationCanceledException();
            }
        }

        /// <summary>
        /// Procura o deslocamento: guarda uma faixa do meio com folga de
        /// AlcanceBusca de cada lado e recalcula a v1 dela para cada
        /// deslocamento k por recorrência —
        ///   v1(k+1) = v1(k) − W(k) + n·s[ini+n+k],   W(k+1) = W(k) − s[ini+k] + s[ini+n+k]
        /// (W é a soma simples da janela). Devolve os k em que a v1 bate com
        /// alguma prensagem, do mais próximo de zero ao mais longe.
        /// </summary>
        public static List<int> ProcurarOffsets(IFonteDisco objFonte, TTocAudio objToc, List<TPrensagem> lstBase, Func<bool> cancelou)
        {
            List<int> lstAchados = new List<int>();
            int n = objToc.QtdFaixas;
            if (n < 3 || lstBase == null || lstBase.Count == 0) return lstAchados;

            // A faixa do meio mais curta: menos memória, e sem as pontas cortadas.
            int intSonda = 1;
            for (int t = 2; t < n - 1; t++)
                if (objToc.AmostrasDaFaixa(t) < objToc.AmostrasDaFaixa(intSonda)) intSonda = t;
            long lngIni = (long)(objToc.lstInicios[intSonda] - objFonte.LbaInicial) * TTocAudio.AmostrasPorSetor;
            int intN = (int)objToc.AmostrasDaFaixa(intSonda);
            long lngJanIni = lngIni - AlcanceBusca;
            uint[] s = new uint[intN + 2 * AlcanceBusca + 1];   // fora do disco fica zero

            objFonte.Reiniciar();
            uint[] buf = new uint[65536];
            long pos = 0;
            int lidos;
            while ((lidos = objFonte.Ler(buf, buf.Length)) > 0)
            {
                long a = Math.Max(pos, lngJanIni), b = Math.Min(pos + lidos, lngJanIni + s.Length);
                for (long q = a; q < b; q++) s[q - lngJanIni] = buf[q - pos];
                pos += lidos;
                if (pos >= lngJanIni + s.Length) break;
                if (cancelou != null && cancelou()) throw new OperationCanceledException();
            }

            HashSet<uint> setBase = new HashSet<uint>();
            foreach (TPrensagem objP in lstBase) setBase.Add(objP.arrCrc[intSonda]);

            // k = −AlcanceBusca: a faixa começa na posição 0 da janela.
            uint v1 = 0, w = 0;
            for (int i = 0; i < intN; i++) { v1 += s[i] * (uint)(i + 1); w += s[i]; }
            for (int k = -AlcanceBusca; ; k++)
            {
                if (setBase.Contains(v1)) lstAchados.Add(k);
                if (k == AlcanceBusca) break;
                int j = k + AlcanceBusca;                    // início da janela no deslocamento k
                uint sSai = s[j], sEntra = s[j + intN];
                v1 = v1 - w + (uint)intN * sEntra;
                w = w - sSai + sEntra;
            }
            lstAchados.Sort((x, y) => Math.Abs(x).CompareTo(Math.Abs(y)));
            return lstAchados;
        }

        /// <summary>
        /// Confere o disco: primeiro como está (deslocamento 0); se não
        /// conferir tudo, procura o deslocamento e confere nele. intOffsetsExtras
        /// são outros candidatos já conhecidos (o offset do drive).
        /// </summary>
        /// <param name="arrConhecidos">
        /// Deslocamentos a tentar já na primeira passada, além do 0 — o offset
        /// do drive. A busca acha só o que a base tem em v1; disco registrado
        /// só em v2 é encontrado por aqui.
        /// </param>
        public static TResultadoAccurateRip Conferir(IFonteDisco objFonte, TTocAudio objToc, int[] arrConhecidos, Func<bool> cancelou)
        {
            TResultadoAccurateRip r = new TResultadoAccurateRip();
            string strUrl, strFalha;
            List<TPrensagem> lstBase = Consultar(objToc, out strUrl, out strFalha);
            r.strUrl = strUrl;
            if (lstBase == null) { r.strFalha = strFalha; return r; }
            r.blnNaBase = lstBase.Count > 0;

            List<int> lstPrimeira = new List<int> { 0 };
            if (arrConhecidos != null)
                foreach (int k in arrConhecidos) if (!lstPrimeira.Contains(k)) lstPrimeira.Add(k);

            uint[,] v1, v2;
            int[] arrK = lstPrimeira.ToArray();
            Calcular(objFonte, objToc, arrK, out v1, out v2, cancelou);
            r = Melhor(r, objToc, lstBase, v1, v2, arrK, strUrl);
            if (!r.blnNaBase || r.TodasConferem) return r;

            List<int> lstK = ProcurarOffsets(objFonte, objToc, lstBase, cancelou);
            lstK.RemoveAll(k => lstPrimeira.Contains(k));
            if (lstK.Count == 0) return r;
            arrK = lstK.GetRange(0, Math.Min(3, lstK.Count)).ToArray();
            Calcular(objFonte, objToc, arrK, out v1, out v2, cancelou);
            return Melhor(r, objToc, lstBase, v1, v2, arrK, strUrl);
        }

        /// <summary>Entre os deslocamentos calculados, o que mais confere (empate: o primeiro, mais próximo de zero).</summary>
        private static TResultadoAccurateRip Melhor(TResultadoAccurateRip rAtual, TTocAudio objToc, List<TPrensagem> lstBase,
                                                    uint[,] v1, uint[,] v2, int[] arrK, string strUrl)
        {
            TResultadoAccurateRip r = rAtual;
            for (int o = 0; o < arrK.Length; o++)
            {
                TResultadoAccurateRip rk = new TResultadoAccurateRip { blnNaBase = lstBase.Count > 0, strUrl = strUrl };
                Preencher(rk, objToc, lstBase, v1, v2, o, arrK[o]);
                if (r.lstFaixas.Count == 0 || rk.QtdConferem > r.QtdConferem) r = rk;
            }
            return r;
        }

        private static void Preencher(TResultadoAccurateRip r, TTocAudio objToc, List<TPrensagem> lstBase,
                                      uint[,] v1, uint[,] v2, int o, int k)
        {
            r.intOffset = k;
            r.lstFaixas.Clear();
            for (int t = 0; t < objToc.QtdFaixas; t++)
            {
                TFaixaAccurateRip f = new TFaixaAccurateRip { intNumero = objToc.intPrimeiraFaixa + t, uintV1 = v1[o, t], uintV2 = v2[o, t] };
                foreach (TPrensagem objP in lstBase)
                {
                    f.intTotalNaBase += objP.arrConfianca[t];
                    if (objP.arrCrc[t] == f.uintV2) { f.intConfianca += objP.arrConfianca[t]; f.strVersao = "v2"; }
                    else if (objP.arrCrc[t] == f.uintV1) { f.intConfianca += objP.arrConfianca[t]; if (f.strVersao == null) f.strVersao = "v1"; }
                }
                r.lstFaixas.Add(f);
            }
        }
    }
}
