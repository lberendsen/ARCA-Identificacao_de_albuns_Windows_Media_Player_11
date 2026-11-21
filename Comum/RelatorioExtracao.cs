using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ARCA.Modelo
{
    /// <summary>
    /// O que a extração física não conseguiu ler, guardado na área de
    /// trabalho ao lado do AudioCD.bin.
    ///
    /// Setor irrecuperável vira silêncio para a imagem sair inteira, e isso
    /// é escolha legítima — desde que se saiba. Sem este arquivo, a contagem
    /// morria na memória do extrator e um disco riscado entrava no acervo
    /// como se fosse cópia exata. Em arquivo, ela sobrevive a fechar o ARCA
    /// entre extrair e aprovar, chega à confirmação da aprovação e ao ledger.
    ///
    /// Formato de texto simples, uma chave por linha, legível à mão:
    /// <code>
    /// setoresMascarados=12
    /// setoresRecuperados=3
    /// faixa=3;mascarados=10
    /// faixa=7;mascarados=2
    /// </code>
    /// </summary>
    public sealed class TRelatorioExtracao
    {
        public const string NomeArquivo = "ARCA_extracao.txt";

        /// <summary>Setores de 2352 bytes num segundo de áudio de CD.</summary>
        public const int SetoresPorSegundo = 75;

        public int intSetoresMascarados;
        public int intSetoresRecuperados;

        /// <summary>Número da faixa → setores substituídos por silêncio nela.</summary>
        public readonly SortedDictionary<int, int> dicMascaradosPorFaixa = new SortedDictionary<int, int>();

        public bool Exata { get { return intSetoresMascarados == 0; } }

        // ---- Conferência bit-perfect (preenchida depois da leitura) ----
        public string strDrive;
        /// <summary>Correção de offset aplicada ao cortar as faixas, em amostras.</summary>
        public int intOffsetAplicado;
        /// <summary>De onde veio o offset (tabela do AccurateRip, confirmado aqui…); null = desconhecido, nenhuma correção.</summary>
        public string strOrigemOffset;
        /// <summary>"na base", "fora da base" ou o motivo de não ter consultado; null = não consultado.</summary>
        public string strAccurateRip;
        /// <summary>Deslocamento em que o AccurateRip conferiu (≠ offset aplicado: outra prensagem).</summary>
        public int intOffsetAccurateRip;
        public readonly SortedDictionary<int, TVerificacaoFaixa> dicVerificacao = new SortedDictionary<int, TVerificacaoFaixa>();

        public TVerificacaoFaixa Verificacao(int intFaixa)
        {
            TVerificacaoFaixa v;
            if (!dicVerificacao.TryGetValue(intFaixa, out v)) { v = new TVerificacaoFaixa { intFaixa = intFaixa }; dicVerificacao[intFaixa] = v; }
            return v;
        }

        /// <summary>Faixas comprovadas pelo AccurateRip.</summary>
        public int QtdComprovadas
        {
            get
            {
                int n = 0;
                foreach (TVerificacaoFaixa v in dicVerificacao.Values) if (v.Nivel == NivelBitPerfect.Comprovada) n++;
                return n;
            }
        }

        /// <summary>Uma linha para a tela, a aprovação e o ledger.</summary>
        public string ResumoBitPerfect()
        {
            int n = dicVerificacao.Count;
            if (n == 0) return "conferência bit-perfect não feita";
            int c = 0, r = 0, x = 0;
            List<string> lstProblemas = new List<string>();
            foreach (TVerificacaoFaixa v in dicVerificacao.Values)
            {
                if (v.Nivel == NivelBitPerfect.Comprovada) c++;
                else if (v.Nivel == NivelBitPerfect.LidaDuasVezes) r++;
                else { x++; lstProblemas.Add("faixa " + v.intFaixa + " (" + v.Motivo() + ")"); }
            }
            string s = string.Format("{0} de {1} faixa(s) comprovada(s) pelo AccurateRip", c, n);
            if (r > 0) s += string.Format("; {0} lida(s) duas vezes, idêntica(s) — boa, mas sem prova externa", r);
            if (x > 0) s += string.Format("; {0} NÃO comprovada(s): {1}", x, string.Join(", ", lstProblemas.ToArray()));
            return s;
        }

        /// <summary>"faixa 3 (10 setores, 0,13 s), faixa 7 (2 setores, 0,03 s)".</summary>
        public string ResumoPorFaixa()
        {
            List<string> lstPartes = new List<string>();
            foreach (KeyValuePair<int, int> kv in dicMascaradosPorFaixa)
                lstPartes.Add(string.Format("faixa {0} ({1} setor{2}, {3:F2} s)", kv.Key, kv.Value,
                                            kv.Value == 1 ? "" : "es", (double)kv.Value / SetoresPorSegundo));
            return lstPartes.Count > 0 ? string.Join(", ", lstPartes.ToArray()) : "faixa não identificada";
        }

        public void Gravar(string strPasta)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("setoresMascarados=").Append(intSetoresMascarados).Append("\r\n");
            sb.Append("setoresRecuperados=").Append(intSetoresRecuperados).Append("\r\n");
            foreach (KeyValuePair<int, int> kv in dicMascaradosPorFaixa)
                sb.Append("faixa=").Append(kv.Key).Append(";mascarados=").Append(kv.Value).Append("\r\n");
            if (strDrive != null) sb.Append("drive=").Append(strDrive).Append("\r\n");
            sb.Append("offset=").Append(intOffsetAplicado.ToString(CultureInfo.InvariantCulture)).Append(";origem=").Append(strOrigemOffset ?? "").Append("\r\n");
            if (strAccurateRip != null)
                sb.Append("accuraterip=").Append(strAccurateRip.Replace(";", ",")).Append(";offset=")
                  .Append(intOffsetAccurateRip.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            foreach (TVerificacaoFaixa v in dicVerificacao.Values) sb.Append(v.ParaLinha()).Append("\r\n");
            File.WriteAllText(Path.Combine(strPasta, NomeArquivo), sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>O relatório da área de trabalho, ou null se não houver (extração anterior a este arquivo, ou falha).</summary>
        public static TRelatorioExtracao Ler(string strPasta)
        {
            string strArquivo = Path.Combine(strPasta, NomeArquivo);
            if (!File.Exists(strArquivo)) return null;
            try
            {
                TRelatorioExtracao objR = new TRelatorioExtracao();
                foreach (string strLinha in File.ReadAllLines(strArquivo, Encoding.UTF8))
                {
                    string strL = strLinha.Trim();
                    if (strL.StartsWith("setoresMascarados=")) objR.intSetoresMascarados = Inteiro(strL.Substring(18));
                    else if (strL.StartsWith("setoresRecuperados=")) objR.intSetoresRecuperados = Inteiro(strL.Substring(19));
                    else if (strL.StartsWith("drive=")) objR.strDrive = strL.Substring(6);
                    else if (strL.StartsWith("offset="))
                    {
                        string[] arrP = strL.Substring(7).Split(';');
                        objR.intOffsetAplicado = Inteiro(arrP[0]);
                        string strO = arrP.Length > 1 && arrP[1].StartsWith("origem=") ? arrP[1].Substring(7) : "";
                        objR.strOrigemOffset = strO.Length > 0 ? strO : null;
                    }
                    else if (strL.StartsWith("accuraterip="))
                    {
                        string[] arrP = strL.Substring(12).Split(';');
                        objR.strAccurateRip = arrP[0];
                        if (arrP.Length > 1 && arrP[1].StartsWith("offset=")) objR.intOffsetAccurateRip = Inteiro(arrP[1].Substring(7));
                    }
                    else if (strL.StartsWith("verif="))
                    {
                        TVerificacaoFaixa v = TVerificacaoFaixa.DeLinha(strL);
                        if (v != null) objR.dicVerificacao[v.intFaixa] = v;
                    }
                    else if (strL.StartsWith("faixa="))
                    {
                        string[] arrPartes = strL.Split(';');
                        if (arrPartes.Length == 2 && arrPartes[1].StartsWith("mascarados="))
                            objR.dicMascaradosPorFaixa[Inteiro(arrPartes[0].Substring(6))] = Inteiro(arrPartes[1].Substring(11));
                    }
                }
                return objR;
            }
            catch { return null; }
        }

        public static void Apagar(string strPasta)
        {
            try { File.Delete(Path.Combine(strPasta, NomeArquivo)); } catch { }
        }

        internal static int Inteiro(string s)
        {
            int n;
            return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
        }
    }

    public enum NivelBitPerfect
    {
        /// <summary>O AccurateRip confere e o WMA é idêntico ao que se leu: prova externa.</summary>
        Comprovada,
        /// <summary>Fora da base (ou sem conferir com ela), mas duas leituras deram o mesmo e o WMA é idêntico.</summary>
        LidaDuasVezes,
        NaoComprovada
    }

    /// <summary>O que se sabe de uma faixa: AccurateRip, releitura, WMA, setores mascarados.</summary>
    public sealed class TVerificacaoFaixa
    {
        public int intFaixa;
        public int intConfiancaAr = -1;          // -1 = não conferido no AccurateRip
        public int intTotalAr;
        public string strVersaoAr;
        public int intReleituraDiferentes = -1;  // -1 = sem releitura
        public int intReleituraIlegiveis;
        public int intMascarados;
        /// <summary>null = WMA não conferido; senão, se é idêntico ao áudio lido.</summary>
        public bool? blnWmaIdentico;
        public string strFalhaWma;

        public NivelBitPerfect Nivel
        {
            get
            {
                if (blnWmaIdentico != true || intMascarados > 0) return NivelBitPerfect.NaoComprovada;
                if (intConfiancaAr > 0) return NivelBitPerfect.Comprovada;
                if (intReleituraDiferentes == 0 && intReleituraIlegiveis == 0) return NivelBitPerfect.LidaDuasVezes;
                return NivelBitPerfect.NaoComprovada;
            }
        }

        /// <summary>Por que não está comprovada, em poucas palavras.</summary>
        public string Motivo()
        {
            if (intMascarados > 0) return intMascarados + " setor(es) ilegível(is)";
            if (blnWmaIdentico == false) return "WMA diferente do lido: " + (strFalhaWma ?? "");
            if (blnWmaIdentico == null) return "WMA não conferido";
            if (intReleituraDiferentes > 0) return "a releitura deu " + intReleituraDiferentes + " setor(es) diferente(s)";
            if (intReleituraIlegiveis > 0) return "a releitura não leu " + intReleituraIlegiveis + " setor(es)";
            if (intConfiancaAr == 0 && intTotalAr > 0) return "não confere com o AccurateRip";
            return "sem conferência";
        }

        /// <summary>Texto para o atributo ARCA/Verification da faixa.</summary>
        public string ParaEtiqueta()
        {
            List<string> lst = new List<string>();
            if (intConfiancaAr > 0) lst.Add(string.Format("AccurateRip {0} (confiança {1})", strVersaoAr, intConfiancaAr));
            else if (intConfiancaAr == 0 && intTotalAr > 0) lst.Add("AccurateRip: não confere");
            else if (intConfiancaAr == 0) lst.Add("AccurateRip: disco fora da base");
            if (intReleituraDiferentes == 0 && intReleituraIlegiveis == 0) lst.Add("lida duas vezes, idêntica");
            else if (intReleituraDiferentes > 0 || intReleituraIlegiveis > 0) lst.Add("releitura diferente");
            if (intMascarados > 0) lst.Add(intMascarados + " setor(es) ilegível(is)");
            if (blnWmaIdentico == true) lst.Add("WMA idêntico ao lido");
            else if (blnWmaIdentico == false) lst.Add("WMA diferente do lido");
            string strNivel = Nivel == NivelBitPerfect.Comprovada ? "BIT-PERFECT COMPROVADO"
                            : Nivel == NivelBitPerfect.LidaDuasVezes ? "provavelmente bit-perfect"
                            : "NÃO comprovado";
            return strNivel + " — " + string.Join("; ", lst.ToArray());
        }

        public string ParaLinha()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "verif={0};ar={1};artotal={2};arversao={3};releitura={4};ilegiveis={5};mascarados={6};wma={7};wmafalha={8}",
                intFaixa, intConfiancaAr, intTotalAr, strVersaoAr ?? "", intReleituraDiferentes, intReleituraIlegiveis, intMascarados,
                blnWmaIdentico == null ? "" : (blnWmaIdentico.Value ? "ok" : "diferente"), (strFalhaWma ?? "").Replace(";", ","));
        }

        public static TVerificacaoFaixa DeLinha(string strLinha)
        {
            TVerificacaoFaixa v = new TVerificacaoFaixa();
            bool blnTem = false;
            foreach (string strParte in strLinha.Split(';'))
            {
                int i = strParte.IndexOf('=');
                if (i < 0) continue;
                string k = strParte.Substring(0, i), x = strParte.Substring(i + 1);
                switch (k)
                {
                    case "verif": v.intFaixa = TRelatorioExtracao.Inteiro(x); blnTem = true; break;
                    case "ar": v.intConfiancaAr = TRelatorioExtracao.Inteiro(x); break;
                    case "artotal": v.intTotalAr = TRelatorioExtracao.Inteiro(x); break;
                    case "arversao": v.strVersaoAr = x.Length > 0 ? x : null; break;
                    case "releitura": v.intReleituraDiferentes = TRelatorioExtracao.Inteiro(x); break;
                    case "ilegiveis": v.intReleituraIlegiveis = TRelatorioExtracao.Inteiro(x); break;
                    case "mascarados": v.intMascarados = TRelatorioExtracao.Inteiro(x); break;
                    case "wma": v.blnWmaIdentico = x == "ok" ? true : x == "diferente" ? (bool?)false : null; break;
                    case "wmafalha": v.strFalhaWma = x.Length > 0 ? x : null; break;
                }
            }
            return blnTem ? v : null;
        }
    }
}
