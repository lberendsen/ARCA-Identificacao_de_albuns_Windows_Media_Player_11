using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ARCA.Modelo
{
    public class TItunesTrackInfo
    {
        public int intTrackNumber { get; set; }
        /// <summary>Disco do álbum (1, 2…): num álbum duplo, a faixa 1 existe uma vez por disco.</summary>
        public int intDiscNumber { get; set; }
        public string strTrackName { get; set; }
        public string strArtistName { get; set; }
        public int intTrackTimeMillis { get; set; }
    }

    public class TItunesAlbumInfo
    {
        public long lngCollectionId { get; set; }
        public string strArtistName { get; set; }
        public string strCollectionName { get; set; }
        public string strPrimaryGenreName { get; set; }
        public string strReleaseDate { get; set; }
        public string strReleaseYear { get; set; }
        public string strArtworkUrl100 { get; set; }
        public string strArtworkUrl600 { get; set; }
        public string strArtworkUrl1400 { get; set; }
        public int intTrackCount { get; set; }
        public string strUpc { get; set; }
        public List<TItunesTrackInfo> lstFaixas { get; set; }

        public TItunesAlbumInfo()
        {
            lstFaixas = new List<TItunesTrackInfo>();
        }
    }

    public static class ItunesResolver
    {
        public static TItunesAlbumInfo ConsultarPorUpc(string strUpc, Action<string> actLog)
        {
            if (string.IsNullOrEmpty(strUpc)) return null;

            string strUrl = string.Format("https://itunes.apple.com/lookup?upc={0}&entity=song", Uri.EscapeDataString(strUpc.Trim()));
            if (actLog != null) actLog(string.Format("iTunes: Consultando por UPC/EAN '{0}'...", strUpc));

            return ExecutarConsultaItunes(strUrl, actLog);
        }

        public static TItunesAlbumInfo ConsultarPorUrlOuId(string strUrlOuId, Action<string> actLog)
        {
            return ConsultarPorUrlOuId(strUrlOuId, 0, actLog);
        }

        /// <summary>
        /// O texto aponta um álbum exato (link do Apple Music/iTunes ou ID
        /// numérico), e não é um termo de busca? Os mesmos três formatos que
        /// ConsultarPorUrlOuId reconhece.
        /// </summary>
        public static bool EhLinkOuId(string strEntrada)
        {
            if (string.IsNullOrEmpty(strEntrada)) return false;
            string strTexto = strEntrada.Trim();
            return Regex.IsMatch(strTexto, @"/album/[^/]+/(\d+)", RegexOptions.IgnoreCase)
                   || Regex.IsMatch(strTexto, @"id=(\d+)", RegexOptions.IgnoreCase)
                   || Regex.IsMatch(strTexto, @"^(\d{6,15})$");
        }

        public static TItunesAlbumInfo ConsultarPorUrlOuId(string strUrlOuId, int intQtdFaixasEsperadas, Action<string> actLog)
        {
            if (string.IsNullOrEmpty(strUrlOuId)) return null;

            string strEntrada = strUrlOuId.Trim();

            Match matchUrl = Regex.Match(strEntrada, @"/album/[^/]+/(\d+)", RegexOptions.IgnoreCase);
            if (!matchUrl.Success)
            {
                matchUrl = Regex.Match(strEntrada, @"id=(\d+)", RegexOptions.IgnoreCase);
            }
            if (!matchUrl.Success)
            {
                matchUrl = Regex.Match(strEntrada, @"^(\d{6,15})$");
            }

            if (matchUrl.Success)
            {
                string strCollectionId = matchUrl.Groups[1].Value;
                if (actLog != null) actLog(string.Format("iTunes: ID de álbum detectado: {0}. Consultando via lookup...", strCollectionId));

                string strUrl = string.Format("https://itunes.apple.com/lookup?id={0}&entity=song", strCollectionId);
                return ExecutarConsultaItunes(strUrl, actLog, intQtdFaixasEsperadas);
            }

            if (actLog != null) actLog(string.Format("iTunes: Buscando álbum por termo '{0}'...", strEntrada));
            return ConsultarAlbum("", strEntrada, intQtdFaixasEsperadas, actLog);
        }

        public static TItunesAlbumInfo ConsultarPorTermo(string strTermo, Action<string> actLog)
        {
            return ConsultarAlbum("", strTermo, 0, actLog);
        }

        public static TItunesAlbumInfo ConsultarAlbum(string strArtista, string strAlbum, int intQtdFaixasEsperadas, Action<string> actLog)
        {
            return ConsultarAlbum(strArtista, strAlbum, intQtdFaixasEsperadas, true, actLog);
        }

        /// <param name="blnExigirContagem">
        /// true (identificação automática): álbum sem um disco com o número de
        /// faixas do CD é recusado — ninguém confere depois. false (janela de
        /// escolha): devolve o disco mais próximo, porque a pessoa vê a duração
        /// de cada faixa ao lado e decide.
        /// </param>
        public static TItunesAlbumInfo ConsultarAlbum(string strArtista, string strAlbum, int intQtdFaixasEsperadas, bool blnExigirContagem, Action<string> actLog)
        {
            if (actLog == null) actLog = delegate { };

            string strArtistaLimpo = (strArtista ?? "").Trim();
            string strAlbumLimpo = (strAlbum ?? "").Trim();

            if (string.IsNullOrEmpty(strArtistaLimpo) && string.IsNullOrEmpty(strAlbumLimpo))
                return null;

            List<string> lstConsultas = new List<string>();

            // 1. Busca combinada: Artista + Álbum
            if (!string.IsNullOrEmpty(strArtistaLimpo) && !string.IsNullOrEmpty(strAlbumLimpo) && strArtistaLimpo != "Desconhecido")
            {
                lstConsultas.Add(strArtistaLimpo + " " + strAlbumLimpo);

                // Normalizações de conectivos: ex: " e " -> " & ", ou remoção de " e "
                if (strArtistaLimpo.IndexOf(" e ", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    lstConsultas.Add(Regex.Replace(strArtistaLimpo, @"\s+e\s+", " & ", RegexOptions.IgnoreCase) + " " + strAlbumLimpo);
                    lstConsultas.Add(Regex.Replace(strArtistaLimpo, @"\s+e\s+", " ", RegexOptions.IgnoreCase) + " " + strAlbumLimpo);

                    // Primeiro artista individual antes de " e "
                    string[] arrPartes = Regex.Split(strArtistaLimpo, @"\s+e\s+", RegexOptions.IgnoreCase);
                    if (arrPartes.Length > 0 && !string.IsNullOrEmpty(arrPartes[0].Trim()))
                    {
                        lstConsultas.Add(arrPartes[0].Trim() + " " + strAlbumLimpo);
                    }
                }
                else if (strArtistaLimpo.IndexOf(" & ", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    string[] arrPartes = Regex.Split(strArtistaLimpo, @"\s+&\s+", RegexOptions.IgnoreCase);
                    if (arrPartes.Length > 0 && !string.IsNullOrEmpty(arrPartes[0].Trim()))
                    {
                        lstConsultas.Add(arrPartes[0].Trim() + " " + strAlbumLimpo);
                    }
                }
            }

            // 2. Busca por Álbum isolado se informado
            if (!string.IsNullOrEmpty(strAlbumLimpo))
            {
                if (!lstConsultas.Contains(strAlbumLimpo))
                    lstConsultas.Add(strAlbumLimpo);
            }
            else if (!string.IsNullOrEmpty(strArtistaLimpo) && strArtistaLimpo != "Desconhecido")
            {
                lstConsultas.Add(strArtistaLimpo);
            }

            List<TItunesAlbumCandidato> lstTodosCandidatos = new List<TItunesAlbumCandidato>();
            HashSet<long> setIdsVistos = new HashSet<long>();

            foreach (string strTermoBusca in lstConsultas)
            {
                actLog(string.Format("iTunes: Buscando candidatos por termo '{0}'...", strTermoBusca));
                List<TItunesAlbumCandidato> lstParcial = BuscarCandidatos(strTermoBusca, 15);
                foreach (TItunesAlbumCandidato cand in lstParcial)
                {
                    if (setIdsVistos.Add(cand.lngCollectionId))
                    {
                        cand.intScore = CalcularPontuacao(cand, strArtistaLimpo, strAlbumLimpo, intQtdFaixasEsperadas);
                        lstTodosCandidatos.Add(cand);
                    }
                }

                // Se já achou um candidato com pontuação de correspondência perfeita, não precisa consultar termos mais vagos
                bool blnAchouExcelente = false;
                foreach (TItunesAlbumCandidato c in lstTodosCandidatos)
                {
                    if (c.intScore >= 180) { blnAchouExcelente = true; break; }
                }
                if (blnAchouExcelente) break;
            }

            if (lstTodosCandidatos.Count == 0)
            {
                actLog("iTunes: Nenhum candidato a álbum encontrado.");
                return null;
            }

            // Ordena os candidatos pela maior pontuação. Se o melhor for recusado
            // (nenhum disco dele tem as faixas deste CD), tenta os seguintes —
            // antes, desistia no primeiro.
            lstTodosCandidatos.Sort((a, b) => b.intScore.CompareTo(a.intScore));
            for (int i = 0; i < lstTodosCandidatos.Count && i < 4; i++)
            {
                TItunesAlbumCandidato candEleito = lstTodosCandidatos[i];
                actLog(string.Format("iTunes: Candidato eleito: '{0}' de '{1}' (Faixas: {2}, ID: {3}, Score: {4}). Carregando faixas...",
                    candEleito.strCollectionName, candEleito.strArtistName, candEleito.intTrackCount, candEleito.lngCollectionId, candEleito.intScore));

                string strUrlLookup = string.Format("https://itunes.apple.com/lookup?id={0}&entity=song", candEleito.lngCollectionId);
                TItunesAlbumInfo objAlbum = ExecutarConsultaItunes(strUrlLookup, actLog, intQtdFaixasEsperadas, blnExigirContagem);
                if (objAlbum != null) return objAlbum;
            }
            actLog("iTunes: nenhum candidato tem um disco com as faixas deste CD.");
            return null;
        }

        private class TItunesAlbumCandidato
        {
            public long lngCollectionId { get; set; }
            public string strArtistName { get; set; }
            public string strCollectionName { get; set; }
            public int intTrackCount { get; set; }
            public string strArtworkUrl100 { get; set; }
            public int intScore { get; set; }
        }

        private static List<TItunesAlbumCandidato> BuscarCandidatos(string strTermo, int intLimit)
        {
            List<TItunesAlbumCandidato> lstCandidatos = new List<TItunesAlbumCandidato>();
            if (string.IsNullOrEmpty(strTermo)) return lstCandidatos;

            string strUrlBusca = string.Format("https://itunes.apple.com/search?term={0}&entity=album&limit={1}",
                Uri.EscapeDataString(strTermo.Trim()), intLimit);

            try
            {
                string strJsonBusca = ExternalMetadataClient.FetchString(strUrlBusca);
                if (string.IsNullOrEmpty(strJsonBusca)) return lstCandidatos;

                JavaScriptSerializer objSerializer = new JavaScriptSerializer();
                Dictionary<string, object> dicBusca = objSerializer.Deserialize<Dictionary<string, object>>(strJsonBusca);
                if (dicBusca == null || !dicBusca.ContainsKey("results")) return lstCandidatos;

                ArrayList lstResultados = dicBusca["results"] as ArrayList;
                if (lstResultados == null) return lstCandidatos;

                foreach (object obj in lstResultados)
                {
                    Dictionary<string, object> d = obj as Dictionary<string, object>;
                    if (d == null || !d.ContainsKey("collectionId")) continue;

                    TItunesAlbumCandidato cand = new TItunesAlbumCandidato();
                    cand.lngCollectionId = Convert.ToInt64(d["collectionId"]);
                    cand.strArtistName = d.ContainsKey("artistName") ? d["artistName"].ToString() : "";
                    cand.strCollectionName = d.ContainsKey("collectionName") ? d["collectionName"].ToString() : "";
                    cand.intTrackCount = d.ContainsKey("trackCount") ? Convert.ToInt32(d["trackCount"]) : 0;
                    cand.strArtworkUrl100 = d.ContainsKey("artworkUrl100") ? d["artworkUrl100"].ToString() : "";
                    lstCandidatos.Add(cand);
                }
            }
            catch { }

            return lstCandidatos;
        }

        private static HashSet<string> Palavras(string strTexto)
        {
            return new HashSet<string>(Regex.Split(strTexto ?? "", @"[^\p{L}\p{N}]+"), StringComparer.Ordinal);
        }

        private static int CalcularPontuacao(TItunesAlbumCandidato cand, string strArtistaEsperado, string strAlbumEsperado, int intQtdFaixasEsperadas)
        {
            int intPontos = 0;

            string strAlbumNormalizadoEsperado = NormalizarTextoParaComparacao(strAlbumEsperado);
            string strAlbumNormalizadoCand = NormalizarTextoParaComparacao(cand.strCollectionName);

            // 1. Comparação de Título do Álbum
            if (!string.IsNullOrEmpty(strAlbumNormalizadoEsperado) && !string.IsNullOrEmpty(strAlbumNormalizadoCand))
            {
                if (strAlbumNormalizadoCand == strAlbumNormalizadoEsperado)
                {
                    intPontos += 100;
                }
                else if (strAlbumNormalizadoCand.StartsWith(strAlbumNormalizadoEsperado) || strAlbumNormalizadoEsperado.StartsWith(strAlbumNormalizadoCand))
                {
                    intPontos += 60;
                }
                else if (strAlbumNormalizadoCand.Contains(strAlbumNormalizadoEsperado))
                {
                    intPontos += 40;
                }
                else
                {
                    // Título sem relação nenhuma com o buscado: não é o álbum, por
                    // mais que o número de faixas coincida (Abbey Road não é o
                    // White Album só por ter 17 faixas).
                    intPontos -= 80;
                }

                // Penalidade para "II", "Vol. 2", "Single", "EP" quando o esperado não
                // os tem — por PALAVRA: por pedaço de texto, "ep" pegava "deep" e
                // " 2" penalizava "Tropicália 2" quando era o próprio álbum.
                HashSet<string> setEsperado = Palavras(strAlbumNormalizadoEsperado), setCand = Palavras(strAlbumNormalizadoCand);
                if (!setEsperado.Contains("ii") && !setEsperado.Contains("2") && (setCand.Contains("ii") || setCand.Contains("2")))
                {
                    intPontos -= 40;
                }
                if (!setEsperado.Contains("single") && setCand.Contains("single"))
                {
                    intPontos -= 50;
                }
                if (!setEsperado.Contains("ep") && setCand.Contains("ep"))
                {
                    intPontos -= 30;
                }
            }

            // 2. Comparação de Quantidade de Faixas
            if (intQtdFaixasEsperadas > 0 && cand.intTrackCount > 0)
            {
                if (cand.intTrackCount == intQtdFaixasEsperadas)
                {
                    intPontos += 100;
                }
                else if (Math.Abs(cand.intTrackCount - intQtdFaixasEsperadas) == 1)
                {
                    intPontos += 30;
                }
                else if (cand.intTrackCount > intQtdFaixasEsperadas)
                {
                    // Mais faixas que o CD: pode ser um álbum de vários discos, e
                    // este CD, um deles. Não penaliza — quem decide é a escolha do
                    // disco, depois de carregar as faixas (EscolherDisco).
                }
                else
                {
                    // Penaliza álbuns cuja quantidade de faixas divirja muito
                    intPontos -= Math.Min(50, Math.Abs(cand.intTrackCount - intQtdFaixasEsperadas) * 5);
                }
            }

            // 3. Comparação de Artista
            string strArtistaNormalizadoEsperado = NormalizarTextoParaComparacao(strArtistaEsperado);
            string strArtistaNormalizadoCand = NormalizarTextoParaComparacao(cand.strArtistName);

            if (!string.IsNullOrEmpty(strArtistaNormalizadoEsperado) && !string.IsNullOrEmpty(strArtistaNormalizadoCand) && strArtistaNormalizadoEsperado != "desconhecido")
            {
                if (strArtistaNormalizadoCand == strArtistaNormalizadoEsperado)
                {
                    intPontos += 50;
                }
                else
                {
                    string[] arrPalavras = strArtistaNormalizadoEsperado.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    int intPalavrasCasadas = 0;
                    foreach (string p in arrPalavras)
                    {
                        if (p.Length >= 3 && strArtistaNormalizadoCand.Contains(p))
                        {
                            intPalavrasCasadas++;
                        }
                    }
                    intPontos += intPalavrasCasadas * 15;
                    // Nenhuma palavra do artista em comum: é outro artista com um
                    // álbum de mesmo nome (o "Back in Black" do Whodini, de 10
                    // faixas, passava à frente do AC/DC).
                    if (intPalavrasCasadas == 0) intPontos -= 60;
                }
            }

            return intPontos;
        }

        private static string NormalizarTextoParaComparacao(string strTexto)
        {
            if (string.IsNullOrEmpty(strTexto)) return "";
            string strLimpo = strTexto.ToLowerInvariant().Trim();

            // Remove acentos
            string strDecomposto = strLimpo.Normalize(NormalizationForm.FormD);
            StringBuilder sb = new StringBuilder();
            foreach (char c in strDecomposto)
            {
                System.Globalization.UnicodeCategory uc = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                if (uc != System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
                    {
                        sb.Append(c);
                    }
                }
            }
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        /// <summary>
        /// Num álbum de vários discos, fica só com o disco que é este CD: o que
        /// tem o número de faixas esperado. Com a contagem conhecida e nenhum
        /// disco (nem o álbum inteiro) batendo, o álbum é recusado — aplicar
        /// títulos de outro disco seria pior que não identificar.
        /// </summary>
        private static bool EscolherDisco(TItunesAlbumInfo objAlbum, int intQtdFaixasEsperadas, bool blnExigirContagem, Action<string> actLog)
        {
            SortedDictionary<int, List<TItunesTrackInfo>> dicDiscos = new SortedDictionary<int, List<TItunesTrackInfo>>();
            foreach (TItunesTrackInfo t in objAlbum.lstFaixas)
            {
                List<TItunesTrackInfo> l;
                if (!dicDiscos.TryGetValue(t.intDiscNumber, out l)) { l = new List<TItunesTrackInfo>(); dicDiscos[t.intDiscNumber] = l; }
                l.Add(t);
            }
            if (intQtdFaixasEsperadas <= 0) return true;
            if (dicDiscos.Count == 1)
            {
                if (objAlbum.lstFaixas.Count == intQtdFaixasEsperadas) return true;
                if (!blnExigirContagem)
                {
                    if (actLog != null) actLog(string.Format("iTunes: o álbum tem {0} faixas e o CD tem {1} — confira as durações antes de escolher.", objAlbum.lstFaixas.Count, intQtdFaixasEsperadas));
                    return true;
                }
                if (actLog != null) actLog(string.Format("iTunes: o álbum tem {0} faixas e o CD tem {1} — não é este disco.", objAlbum.lstFaixas.Count, intQtdFaixasEsperadas));
                return false;
            }
            List<int> lstQueBatem = new List<int>();
            foreach (KeyValuePair<int, List<TItunesTrackInfo>> kv in dicDiscos)
                if (kv.Value.Count == intQtdFaixasEsperadas) lstQueBatem.Add(kv.Key);
            if (lstQueBatem.Count == 0 && !blnExigirContagem)
            {
                // A pessoa vai conferir: o disco de contagem mais próxima.
                int intMelhor = -1;
                foreach (KeyValuePair<int, List<TItunesTrackInfo>> kv in dicDiscos)
                    if (intMelhor < 0 || Math.Abs(kv.Value.Count - intQtdFaixasEsperadas) < Math.Abs(dicDiscos[intMelhor].Count - intQtdFaixasEsperadas)) intMelhor = kv.Key;
                lstQueBatem.Add(intMelhor);
            }
            if (lstQueBatem.Count == 0)
            {
                if (actLog != null) actLog(string.Format("iTunes: o álbum tem {0} discos e nenhum tem as {1} faixas deste CD.", dicDiscos.Count, intQtdFaixasEsperadas));
                return false;
            }
            if (lstQueBatem.Count > 1 && actLog != null)
                actLog(string.Format("iTunes: {0} discos do álbum têm {1} faixas; usando o disco {2} — confira os títulos.", lstQueBatem.Count, intQtdFaixasEsperadas, lstQueBatem[0]));
            else if (actLog != null)
                actLog(string.Format("iTunes: álbum de {0} discos; este CD é o disco {1}.", dicDiscos.Count, lstQueBatem[0]));
            objAlbum.lstFaixas = dicDiscos[lstQueBatem[0]];
            return true;
        }

        private static TItunesAlbumInfo ExecutarConsultaItunes(string strUrl, Action<string> actLog, int intQtdFaixasEsperadas = 0, bool blnExigirContagem = true)
        {
            try
            {
                string strJson = ExternalMetadataClient.FetchString(strUrl);
                if (string.IsNullOrEmpty(strJson)) return null;

                JavaScriptSerializer objSerializer = new JavaScriptSerializer();
                Dictionary<string, object> dicResposta = objSerializer.Deserialize<Dictionary<string, object>>(strJson);
                if (dicResposta == null || !dicResposta.ContainsKey("results")) return null;

                ArrayList lstResultados = dicResposta["results"] as ArrayList;
                if (lstResultados == null || lstResultados.Count == 0) return null;

                TItunesAlbumInfo objAlbum = null;

                foreach (object objItem in lstResultados)
                {
                    Dictionary<string, object> dicItem = objItem as Dictionary<string, object>;
                    if (dicItem == null) continue;

                    string strWrapperType = dicItem.ContainsKey("wrapperType") ? dicItem["wrapperType"].ToString() : "";

                    if (strWrapperType == "collection")
                    {
                        objAlbum = new TItunesAlbumInfo();
                        if (dicItem.ContainsKey("collectionId")) objAlbum.lngCollectionId = Convert.ToInt64(dicItem["collectionId"]);
                        if (dicItem.ContainsKey("artistName")) objAlbum.strArtistName = dicItem["artistName"].ToString();
                        if (dicItem.ContainsKey("collectionName")) objAlbum.strCollectionName = dicItem["collectionName"].ToString();
                        if (dicItem.ContainsKey("primaryGenreName")) objAlbum.strPrimaryGenreName = dicItem["primaryGenreName"].ToString();
                        if (dicItem.ContainsKey("trackCount")) objAlbum.intTrackCount = Convert.ToInt32(dicItem["trackCount"]);

                        if (dicItem.ContainsKey("releaseDate"))
                        {
                            string strRelDate = dicItem["releaseDate"].ToString();
                            objAlbum.strReleaseDate = strRelDate;
                            if (strRelDate.Length >= 4) objAlbum.strReleaseYear = strRelDate.Substring(0, 4);
                        }

                        if (dicItem.ContainsKey("artworkUrl100"))
                        {
                            string strArt100 = dicItem["artworkUrl100"].ToString();
                            objAlbum.strArtworkUrl100 = strArt100;
                            objAlbum.strArtworkUrl600 = strArt100.Replace("100x100bb", "600x600bb");
                            objAlbum.strArtworkUrl1400 = strArt100.Replace("100x100bb", "1400x1400bb");
                        }
                    }
                    else if (strWrapperType == "track" &&
                             (!dicItem.ContainsKey("kind") || dicItem["kind"] == null || dicItem["kind"].ToString() == "song"))
                    {
                        // Só música: edições deluxe listam videoclipes e encartes
                        // digitais como "faixas", e eles desmontariam a escolha do disco.
                        if (objAlbum == null) objAlbum = new TItunesAlbumInfo();

                        TItunesTrackInfo objTrack = new TItunesTrackInfo();
                        if (dicItem.ContainsKey("trackNumber")) objTrack.intTrackNumber = Convert.ToInt32(dicItem["trackNumber"]);
                        objTrack.intDiscNumber = dicItem.ContainsKey("discNumber") ? Convert.ToInt32(dicItem["discNumber"]) : 1;
                        if (dicItem.ContainsKey("trackName")) objTrack.strTrackName = dicItem["trackName"].ToString();
                        if (dicItem.ContainsKey("artistName")) objTrack.strArtistName = dicItem["artistName"].ToString();
                        if (dicItem.ContainsKey("trackTimeMillis")) objTrack.intTrackTimeMillis = Convert.ToInt32(dicItem["trackTimeMillis"]);

                        objAlbum.lstFaixas.Add(objTrack);
                    }
                }

                if (objAlbum != null && objAlbum.lstFaixas.Count > 0)
                {
                    // Disco e depois faixa: ordenar só pela faixa intercalava os
                    // discos de um álbum duplo (1, 1, 2, 2…).
                    objAlbum.lstFaixas.Sort((a, b) => a.intDiscNumber != b.intDiscNumber
                        ? a.intDiscNumber.CompareTo(b.intDiscNumber) : a.intTrackNumber.CompareTo(b.intTrackNumber));
                    if (!EscolherDisco(objAlbum, intQtdFaixasEsperadas, blnExigirContagem, actLog)) return null;
                }

                return objAlbum;
            }
            catch (Exception ex)
            {
                if (actLog != null) actLog(string.Format("iTunes: Erro durante requisição: {0}", ex.Message));
                return null;
            }
        }
    }
}
