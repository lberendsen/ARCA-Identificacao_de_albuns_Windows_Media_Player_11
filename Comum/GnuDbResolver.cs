using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ARCA.Modelo
{
    public static class GnuDbResolver
    {
        private static int CddbSum(int n)
        {
            int ret = 0;
            while (n > 0)
            {
                ret += (n % 10);
                n /= 10;
            }
            return ret;
        }

        /// <summary>
        /// Consulta o GnuDB (freedb) pelo índice do disco, montado do
        /// AudioCD.cue com a mesma regra do AccurateRip (TTocAudio) — que já
        /// confere contra discos reais. Antes o ID era calculado sem os 150
        /// setores do pré-gap (não achava o disco, ou achava outro), o CD Extra
        /// ia com o fim errado, e sem o .bin o fim do disco era inventado (+3 min).
        /// Agora, sem fim conhecido, não consulta.
        /// </summary>
        public static string ResolveToc(string strCue, long lngBytesBin, out string artist, out string album, out string genre, out string year, out List<string> trackTitles)
        {
            artist = null;
            album = null;
            genre = null;
            year = null;
            trackTitles = new List<string>();

            TTocAudio objToc = TTocAudio.DeCue(strCue, lngBytesBin);
            if (objToc == null || objToc.QtdFaixas == 0) return "Sem faixas";
            if (objToc.intLeadOut <= objToc.lstInicios[objToc.QtdFaixas - 1])
                return "O fim do disco é desconhecido (sem lead-out no CUE e sem .bin): consultar o GnuDB com um fim inventado acharia outro disco.";
            int numTracks = objToc.QtdFaixas;

            // freedb conta todas as faixas, inclusive a de dados, com os 150
            // setores do pré-gap; o fim é o lead-out físico do disco.
            List<int> lstTodas = new List<int>(objToc.lstInicios);
            if (objToc.intDadosAntes >= 0) lstTodas.Insert(0, objToc.intDadosAntes);
            int intFim = objToc.intLeadOut;
            if (objToc.intInicioDados >= 0) { lstTodas.Add(objToc.intInicioDados); intFim = objToc.intLeadOutFisico > 0 ? objToc.intLeadOutFisico : intFim; }
            uint id1, id2, idFreedb;
            AccurateRip.CalcularIds(objToc, out id1, out id2, out idFreedb);
            string discIdHex = idFreedb.ToString("x8");
            int totalSeconds = (intFim + 150) / 75;

            StringBuilder queryParams = new StringBuilder();
            queryParams.Append(discIdHex).Append("+").Append(lstTodas.Count);
            foreach (int intLba in lstTodas) queryParams.Append("+").Append(intLba + 150);
            queryParams.Append("+").Append(totalSeconds);

            string urlQuery = string.Format("https://gnudb.gnudb.org/~cddb/cddb.cgi?cmd=cddb+query+{0}&hello=anon+anon.com+ExactAudioCopy+1.3&proto=6", queryParams.ToString());

            string responseQuery = FetchUrl(urlQuery);
            if (string.IsNullOrEmpty(responseQuery)) return "Falha ao conectar no GnuDB.";

            string[] lines = responseQuery.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return "Resposta vazia do GnuDB.";

            string firstLine = lines[0];
            string category = null;
            string finalDiscId = null;
            bool blnInexata = firstLine.StartsWith("211");

            if (firstLine.StartsWith("200"))
            {
                string[] parts = firstLine.Split(' ');
                if (parts.Length >= 3)
                {
                    category = parts[1];
                    finalDiscId = parts[2];
                }
            }
            else if (firstLine.StartsWith("210") || blnInexata)
            {
                if (lines.Length > 1 && !lines[1].StartsWith("."))
                {
                    string[] parts = lines[1].Split(' ');
                    if (parts.Length >= 2)
                    {
                        category = parts[0];
                        finalDiscId = parts[1];
                    }
                }
            }

            if (category == null || finalDiscId == null)
                return "Nenhuma correspondência exata encontrada no GnuDB.";

            string urlRead = string.Format("https://gnudb.gnudb.org/~cddb/cddb.cgi?cmd=cddb+read+{0}+{1}&hello=anon+anon.com+ExactAudioCopy+1.3&proto=6", category, finalDiscId);
            string responseRead = FetchUrl(urlRead);
            if (string.IsNullOrEmpty(responseRead)) return "Falha ao ler os metadados do GnuDB.";

            string[] readLines = responseRead.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            // Correspondência inexata (211) é "parecido", não "este": confere
            // as posições das faixas que o próprio registro traz (comentário
            // # Track frame offsets) com as do disco — mesma quantidade e cada
            // uma a até 2 s. Não conferindo, é outro disco.
            if (blnInexata)
            {
                List<int> lstRegistro = new List<int>();
                bool blnNaLista = false;
                foreach (string line in readLines)
                {
                    if (line.StartsWith("# Track frame offsets", StringComparison.OrdinalIgnoreCase)) { blnNaLista = true; continue; }
                    if (!blnNaLista) continue;
                    Match mOff = Regex.Match(line, @"^#\s*(\d+)\s*$");
                    if (mOff.Success) lstRegistro.Add(int.Parse(mOff.Groups[1].Value));
                    else if (lstRegistro.Count > 0) break;
                }
                bool blnConfere = lstRegistro.Count == lstTodas.Count;
                for (int i = 0; blnConfere && i < lstRegistro.Count; i++)
                    if (Math.Abs(lstRegistro[i] - (lstTodas[i] + 150)) > 150) blnConfere = false;
                if (!blnConfere)
                    return string.Format("O GnuDB só achou um disco parecido ({0} faixas no registro, {1} no CD, ou posições diferentes): não é este.", lstRegistro.Count, lstTodas.Count);
            }
            
            Dictionary<int, string> tt = new Dictionary<int, string>();
            
            // No formato xmcd, título longo vem em várias linhas com a mesma
            // chave, e o texto continua de uma para a outra (às vezes no meio de
            // uma palavra). Antes, cada linha sobrescrevia a anterior.
            System.Text.StringBuilder sbDTitle = new System.Text.StringBuilder();
            foreach (string line in readLines)
                if (line.StartsWith("DTITLE=")) sbDTitle.Append(line.Substring(7));

            foreach (string line in readLines)
            {
                if (line.StartsWith("DTITLE="))
                {
                    if (sbDTitle.Length == 0) continue;   // já tratado: o título inteiro, uma vez só
                    string dTitle = sbDTitle.ToString();
                    sbDTitle.Length = 0;
                    int splitIdx = dTitle.IndexOf(" / ");
                    if (splitIdx != -1)
                    {
                        artist = dTitle.Substring(0, splitIdx).Trim();
                        album = dTitle.Substring(splitIdx + 3).Trim();
                    }
                    else
                    {
                        artist = "Desconhecido";
                        album = dTitle.Trim();
                    }
                    // O título vai para as faixas como o registro o traz. Limpar
                    // aqui (tirar o artista do começo, apagar parênteses do fim)
                    // era o ARCA decidindo o nome do álbum: "Queen Greatest Hits"
                    // virava "Greatest Hits"; "(Vol. 2)" sumia. A limpeza serve
                    // só para BUSCAR (DiscogsClient), nunca para gravar.
                }
                else if (line.StartsWith("DYEAR="))
                {
                    year = line.Substring(6).Trim();
                }
                else if (line.StartsWith("DGENRE="))
                {
                    genre = line.Substring(7).Trim();
                }
                else if (line.StartsWith("TTITLE"))
                {
                    Match m = Regex.Match(line, @"^TTITLE(\d+)=(.*)");
                    if (m.Success)
                    {
                        int tIdx = int.Parse(m.Groups[1].Value);
                        string strAntes;
                        tt[tIdx] = tt.TryGetValue(tIdx, out strAntes) ? strAntes + m.Groups[2].Value : m.Groups[2].Value;
                    }
                }
            }

            for (int i = 0; i < numTracks; i++)
            {
                if (tt.ContainsKey(i) && tt[i].Trim().Length > 0)
                    trackTitles.Add(tt[i].Trim());   // aparado só depois de juntar as linhas
                else
                    trackTitles.Add("Faixa " + (i + 1));
            }
            
            for (int i = 0; i < trackTitles.Count; i++)
            {
                if (!string.IsNullOrEmpty(artist) && artist != "Desconhecido")
                {
                    string prefixSlash = artist + " / ";
                    if (trackTitles[i].StartsWith(prefixSlash, StringComparison.OrdinalIgnoreCase))
                        trackTitles[i] = trackTitles[i].Substring(prefixSlash.Length).Trim();

                    string prefixDash = artist + " - ";
                    if (trackTitles[i].StartsWith(prefixDash, StringComparison.OrdinalIgnoreCase))
                        trackTitles[i] = trackTitles[i].Substring(prefixDash.Length).Trim();
                }
            }

            return "OK";
        }

        private static string FetchUrl(string url)
        {
            try
            {
                return ExternalMetadataClient.FetchString(url);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Erro GnuDB: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Versão "enxuta" do título, SÓ para montar termo de busca: tira o
        /// artista do começo (com separador) e parênteses do fim, que costumam
        /// atrapalhar a procura. Nunca usar o resultado como título gravado —
        /// o nome do álbum é o que a fonte diz.
        /// </summary>
        public static string HigienizarTituloAlbum(string strArtista, string strAlbum)
        {
            if (string.IsNullOrEmpty(strAlbum)) return string.Empty;
            string strLimpo = strAlbum.Trim();

            if (!string.IsNullOrEmpty(strArtista) && strArtista != "Desconhecido")
            {
                // Só tira o artista do começo quando há um separador de verdade
                // ("Queen - Greatest Hits"). Com o separador opcional, bastava um
                // espaço: "Queen II" virava "II", "Led Zeppelin IV" virava "IV".
                string strTentativaPrefixo = Regex.Replace(strLimpo, @"^" + Regex.Escape(strArtista) + @"\s*[-=:/_]\s*", "", RegexOptions.IgnoreCase);
                if (!string.IsNullOrWhiteSpace(strTentativaPrefixo))
                {
                    strLimpo = strTentativaPrefixo;
                }

                string strArtistaSemEspacos = Regex.Replace(strArtista, @"\s+", "");
                strLimpo = Regex.Replace(strLimpo, @"^" + Regex.Escape(strArtistaSemEspacos) + @"\s*[-=:/_]\s*", "", RegexOptions.IgnoreCase);

                string strArtistaSemAcentoNemEspaco = RemoverAcentos(strArtistaSemEspacos);
                string strLimpoSemAcento = RemoverAcentos(strLimpo);
                if (strLimpoSemAcento.StartsWith(strArtistaSemAcentoNemEspaco, StringComparison.OrdinalIgnoreCase))
                {
                    int intTamPrefixo = strArtistaSemAcentoNemEspaco.Length;
                    if (strLimpo.Length > intTamPrefixo && (strLimpo[intTamPrefixo] == '=' || strLimpo[intTamPrefixo] == '-' || strLimpo[intTamPrefixo] == ':' || strLimpo[intTamPrefixo] == '_' || strLimpo[intTamPrefixo] == '/'))
                    {
                        strLimpo = strLimpo.Substring(intTamPrefixo + 1).Trim();
                    }
                }
            }

            strLimpo = Regex.Replace(strLimpo, @"\s*\((?!(?:ao vivo|live|acustico|acoustic|remastered|deluxe)\b)[^\)]+\)\s*$", "", RegexOptions.IgnoreCase);
            return strLimpo.Trim();
        }

        private static string RemoverAcentos(string strTexto)
        {
            if (string.IsNullOrEmpty(strTexto)) return string.Empty;
            string strNormalizada = strTexto.Normalize(System.Text.NormalizationForm.FormD);
            StringBuilder sbResultado = new StringBuilder();
            foreach (char chrC in strNormalizada)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(chrC) != System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    sbResultado.Append(chrC);
                }
            }
            return sbResultado.ToString().Normalize(System.Text.NormalizationForm.FormC);
        }
    }
}
