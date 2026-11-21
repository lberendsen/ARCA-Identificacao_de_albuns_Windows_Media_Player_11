using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace ARCA.Modelo
{
    /// <summary>
    /// O deslocamento de leitura (read offset) de cada modelo de drive.
    ///
    /// Todo drive entrega o áudio deslocado por um número fixo de amostras;
    /// sem corrigir, a faixa sai "igual, mas fora do lugar" — as fronteiras
    /// entre faixas ficam algumas amostras adiantadas ou atrasadas. A
    /// correção é sempre a mesma para o mesmo modelo.
    ///
    /// De onde vem, nesta ordem:
    /// 1. offsets.txt em %LOCALAPPDATA%\ARCA — o que este ARCA já confirmou
    ///    com o AccurateRip, ou o que a pessoa escreveu ali;
    /// 2. a tabela pública do AccurateRip (DriveOffsets.bin), baixada uma vez
    ///    e guardada na mesma pasta.
    /// </summary>
    public static class OffsetDrive
    {
        private const string UrlTabela = "http://www.accuraterip.com/accuraterip/DriveOffsets.bin";
        private const int TamanhoRegistro = 69;   // offset (int16) + nome (32) + 35 bytes que não se usam

        public static string Pasta
        {
            get
            {
                string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ARCA");
                Directory.CreateDirectory(p);
                return p;
            }
        }

        /// <summary>"PLEXTOR |BD-R   PX-B900A " → "PLEXTOR|BD-RPX-B900A": sem espaços, maiúsculas.</summary>
        public static string Chave(string strFabricante, string strModelo)
        {
            return (Sem(strFabricante) + "|" + Sem(strModelo)).ToUpperInvariant();
        }

        private static string Sem(string s)
        {
            return (s ?? "").Replace(" ", "").Replace("\0", "");
        }

        /// <summary>O offset do drive, ou null se não se sabe. strOrigem diz de onde veio.</summary>
        public static int? Obter(string strFabricante, string strModelo, out string strOrigem)
        {
            strOrigem = null;
            if (string.IsNullOrEmpty(strModelo)) return null;
            string strChave = Chave(strFabricante, strModelo);

            Dictionary<string, KeyValuePair<int, string>> dicLocal = LerLocal();
            KeyValuePair<int, string> kv;
            if (dicLocal.TryGetValue(strChave, out kv)) { strOrigem = kv.Value; return kv.Key; }

            byte[] arrTabela = Tabela();
            if (arrTabela == null) return null;
            for (int p = 0; p + TamanhoRegistro <= arrTabela.Length; p += TamanhoRegistro)
            {
                string strNome = Encoding.ASCII.GetString(arrTabela, p + 2, 32).Split('\0')[0];
                int i = strNome.IndexOf(" - ", StringComparison.Ordinal);
                if (i < 0) continue;
                if (Chave(strNome.Substring(0, i), strNome.Substring(i + 3)) != strChave) continue;
                strOrigem = "tabela do AccurateRip";
                return BitConverter.ToInt16(arrTabela, p);
            }
            return null;
        }

        /// <summary>
        /// Guarda o offset confirmado (com o AccurateRip) para este modelo.
        /// Vale mais que a tabela: é deste drive, medido aqui.
        /// </summary>
        public static void Registrar(string strFabricante, string strModelo, int intOffset, string strOrigem)
        {
            Dictionary<string, KeyValuePair<int, string>> dic = LerLocal();
            dic[Chave(strFabricante, strModelo)] = new KeyValuePair<int, string>(intOffset, strOrigem);
            StringBuilder sb = new StringBuilder();
            sb.Append("# Offset de leitura por modelo de drive. Uma linha por drive: CHAVE<TAB>offset<TAB>origem\r\n");
            foreach (KeyValuePair<string, KeyValuePair<int, string>> e in dic)
                sb.Append(e.Key).Append('\t').Append(e.Value.Key.ToString("+0;-0;0", CultureInfo.InvariantCulture))
                  .Append('\t').Append((e.Value.Value ?? "").Replace('\t', ' ')).Append("\r\n");
            File.WriteAllText(Path.Combine(Pasta, "offsets.txt"), sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>
        /// Deslocamentos em que álbuns do acervo já conferiram, do mais comum
        /// ao menos. São bons candidatos para os próximos: o acervo antigo foi
        /// ripado por poucos drives, sem correção.
        /// </summary>
        public static int[] OffsetsDoAcervo()
        {
            List<KeyValuePair<int, int>> lst = new List<KeyValuePair<int, int>>();
            try
            {
                string strArq = Path.Combine(Pasta, "offsets_acervo.txt");
                if (File.Exists(strArq))
                    foreach (string strLinha in File.ReadAllLines(strArq))
                    {
                        string[] arr = strLinha.Split('\t');
                        int k, n;
                        if (arr.Length == 2 && int.TryParse(arr[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out k)
                            && int.TryParse(arr[1], out n)) lst.Add(new KeyValuePair<int, int>(k, n));
                    }
            }
            catch { }
            lst.Sort((x, y) => y.Value.CompareTo(x.Value));
            List<int> lstK = new List<int>();
            for (int i = 0; i < lst.Count && i < 4; i++) lstK.Add(lst[i].Key);
            return lstK.ToArray();
        }

        public static void RegistrarOffsetDoAcervo(int intOffset)
        {
            try
            {
                string strArq = Path.Combine(Pasta, "offsets_acervo.txt");
                Dictionary<int, int> dic = new Dictionary<int, int>();
                if (File.Exists(strArq))
                    foreach (string strLinha in File.ReadAllLines(strArq))
                    {
                        string[] arr = strLinha.Split('\t');
                        int k, n;
                        if (arr.Length == 2 && int.TryParse(arr[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out k)
                            && int.TryParse(arr[1], out n)) dic[k] = n;
                    }
                int intAtual;
                dic.TryGetValue(intOffset, out intAtual);
                dic[intOffset] = intAtual + 1;
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<int, int> kv in dic) sb.Append(kv.Key.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(kv.Value).Append("\r\n");
                File.WriteAllText(strArq, sb.ToString());
            }
            catch { }
        }

        private static Dictionary<string, KeyValuePair<int, string>> LerLocal()
        {
            Dictionary<string, KeyValuePair<int, string>> dic = new Dictionary<string, KeyValuePair<int, string>>();
            try
            {
                string strArq = Path.Combine(Pasta, "offsets.txt");
                if (!File.Exists(strArq)) return dic;
                foreach (string strLinha in File.ReadAllLines(strArq, Encoding.UTF8))
                {
                    if (strLinha.StartsWith("#")) continue;
                    string[] arr = strLinha.Split('\t');
                    int n;
                    if (arr.Length >= 2 && int.TryParse(arr[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n))
                        dic[arr[0].Trim()] = new KeyValuePair<int, string>(n, arr.Length > 2 ? arr[2] : "offsets.txt");
                }
            }
            catch { }
            return dic;
        }

        /// <summary>A tabela do AccurateRip: guardada, ou baixada na primeira vez (e a cada 6 meses).</summary>
        private static byte[] Tabela()
        {
            string strArq = Path.Combine(Pasta, "DriveOffsets.bin");
            try
            {
                if (File.Exists(strArq) && (DateTime.Now - File.GetLastWriteTime(strArq)).TotalDays < 180)
                    return File.ReadAllBytes(strArq);
            }
            catch { }
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(UrlTabela);
                req.Proxy = NetworkConfiguration.CreateProxy();
                req.UserAgent = "ARCA";
                req.Timeout = 30000;
                using (WebResponse resp = NetworkConfiguration.Responder(req))
                using (Stream s = resp.GetResponseStream())
                using (MemoryStream m = new MemoryStream())
                {
                    s.CopyTo(m);
                    byte[] arr = m.ToArray();
                    if (arr.Length >= TamanhoRegistro && arr.Length % TamanhoRegistro == 0)
                    {
                        File.WriteAllBytes(strArq, arr);
                        return arr;
                    }
                }
            }
            catch { }
            // Sem rede: a cópia antiga ainda serve.
            try { if (File.Exists(strArq)) return File.ReadAllBytes(strArq); } catch { }
            return null;
        }
    }
}
