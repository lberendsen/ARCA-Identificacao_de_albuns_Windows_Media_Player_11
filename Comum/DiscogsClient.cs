using System;
using System.Collections;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ARCA.Modelo
{
    /// <summary>
    /// Estrutura de dados contendo os metadados consolidados obtidos a partir do Discogs.
    /// Utiliza estritamente Notação Húngara para todos os campos.
    /// </summary>
    public class TDiscogsReleaseInfo
    {
        public string strArtist;
        public string strAlbum;
        public string strYear;
        public List<string> lstGenres = new List<string>();
        public List<string> lstStyles = new List<string>();
        public List<string> lstTrackTitles = new List<string>();
        public List<string> lstTrackArtists = new List<string>();
        /// <summary>Compositor de cada faixa, alinhado com lstTrackTitles ("" quando não há).</summary>
        public List<string> lstTrackComposers = new List<string>();
        /// <summary>Duração de cada faixa em segundos, alinhada com lstTrackTitles (-1 quando o Discogs não informa).</summary>
        public List<int> lstTrackDurations = new List<int>();
        /// <summary>Gravadora e número de catálogo ("LaserLight Digital 15 688"): é o que distingue prensagens.</summary>
        public string strGravadora;
        public string strPais;
        public string strCoverUrl;
        public byte[] arrCoverBytes;
        public int intReleaseId;
    }

    /// <summary>
    /// Cliente isolado responsável pela comunicação com a API REST v2 do Discogs.
    /// Respeita o Princípio de Responsabilidade Única (SRP) e gerencia autenticação por token pessoal,
    /// cabeçalho User-Agent obrigatório, proxy da rede e download autenticado de imagens.
    /// </summary>
    public static class DiscogsClient
    {
        private const string STR_API_BASE = "https://api.discogs.com";
        private const string STR_USER_AGENT = "ARCA-Identificacao/1.0 ( https://github.com/lberendsen/ARCA-Identificacao_de_albuns_Windows_Media_Player_11 )";


        /// <summary>
        /// O token pessoal do Discogs: da configuração do programa (DiscogsToken), da variável de
        /// ambiente ARCA_DISCOGS_TOKEN ou do arquivo discogs.chave ao lado do executável. Nunca do
        /// código: cada instalação usa o seu (https://www.discogs.com/settings/developers).
        /// </summary>
        public static string ObterTokenConfigurado()
        {
            try
            {
                string strToken = ConfigurationManager.AppSettings["DiscogsToken"];
                if (!string.IsNullOrEmpty(strToken)) return strToken.Trim();
            }
            catch { }
            string strAmbiente = Environment.GetEnvironmentVariable("ARCA_DISCOGS_TOKEN");
            if (!string.IsNullOrEmpty(strAmbiente)) return strAmbiente.Trim();
            try
            {
                string strArquivo = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "discogs.chave");
                if (System.IO.File.Exists(strArquivo)) return System.IO.File.ReadAllText(strArquivo).Trim();
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Efetua a busca e consolidação de metadados de um álbum no Discogs.
        /// Prioriza lançamentos cujo número de faixas coincida com a quantidade esperada do CD físico.
        /// </summary>
        /// <param name="strArtista">Nome do artista candidato obtido pelo MusicBrainz ou GnuDb.</param>
        /// <param name="strAlbum">Título do álbum candidato.</param>
        /// <param name="intQtdFaixasEsperadas">Quantidade de faixas do CD físico lidas no CUE.</param>
        /// <param name="actLog">Callback opcional para mensagens detalhadas de log.</param>
        /// <returns>Objeto TDiscogsReleaseInfo preenchido, ou null caso não encontrado.</returns>
        public static TDiscogsReleaseInfo ConsultarLancamento(string strArtista, string strAlbum, int intQtdFaixasEsperadas, Action<string> actLog)
        {
            if (actLog == null) actLog = delegate { };

            string strToken = ObterTokenConfigurado();
            if (string.IsNullOrEmpty(strToken))
            {
                actLog("Discogs: Token de autenticação não configurado no App.config. Pulando consulta ao Discogs.");
                return null;
            }

            // Basta um dos dois. Sem álbum, busca na discografia do artista e
            // escolhe pela contagem de faixas; sem artista, busca pelo título.
            bool blnSemArtista = string.IsNullOrEmpty(strArtista) || strArtista.Trim().Length == 0;
            bool blnSemAlbum = string.IsNullOrEmpty(strAlbum) || strAlbum.Trim().Length == 0;
            if (blnSemArtista && blnSemAlbum)
            {
                actLog("Discogs: Artista e Álbum vazios. Não é possível realizar a busca.");
                return null;
            }

            try
            {
                // Limpeza suave de termos que costumam poluir a busca no Discogs (ex: "[Live]", "(Disc 1)")
                string strArtistaLimpo = blnSemArtista ? "" : LimparTermoDeBusca(strArtista);
                string strAlbumLimpo = blnSemAlbum ? "" : LimparTermoDeBusca(strAlbum);
                if (!blnSemAlbum) strAlbumLimpo = GnuDbResolver.HigienizarTituloAlbum(strArtistaLimpo, strAlbumLimpo);

                // Normalização para Coletâneas / Vários Intérpretes (Discogs cataloga universalmente como "Various")
                string strArtistaSemAcentos = RemoverAcentos(strArtistaLimpo);
                // Por expressão, não por lista de grafias exatas: "Vários Artistas",
                // "Diversos", "Vários Intérpretes", "Artistas Variados" ficavam de fora.
                bool blnArtistaBuscaColetanea = System.Text.RegularExpressions.Regex.IsMatch(strArtistaSemAcentos.Trim(),
                    @"^(va|v\.\s*a\.?|various(\s+artists?)?|varios(\s+(artistas|interpretes))?|diversos(\s+(artistas|interpretes))?|" +
                    @"artistas\s+(variados|diversos)|coletanea|compilation)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (blnArtistaBuscaColetanea)
                {
                    strArtistaLimpo = "Various";
                }

                actLog(string.Format("Discogs: Consultando API para '{0} - {1}' (faixas esperadas: {2})...", strArtistaLimpo, strAlbumLimpo, intQtdFaixasEsperadas));

                JavaScriptSerializer jssSerializer = new JavaScriptSerializer();
                ArrayList arrResultados = null;
                string strJsonBusca = null;

                // Tentativa 1: artista + título (só com os dois)
                if (!blnSemArtista && !blnSemAlbum)
                {
                    string strUrlBusca = string.Format(
                        "{0}/database/search?artist={1}&release_title={2}&type=release",
                        STR_API_BASE,
                        Uri.EscapeDataString(strArtistaLimpo),
                        Uri.EscapeDataString(strAlbumLimpo)
                    );
                    strJsonBusca = ExecutarRequisicaoJson(strUrlBusca, strToken);
                }

                if (!string.IsNullOrEmpty(strJsonBusca))
                {
                    Dictionary<string, object> dicBusca = jssSerializer.Deserialize<Dictionary<string, object>>(strJsonBusca);
                    if (dicBusca != null && dicBusca.ContainsKey("results"))
                    {
                        arrResultados = dicBusca["results"] as ArrayList;
                    }
                }

                // Tentativa 2: Busca direta por release_title caso a busca inicial não tenha achado
                if ((arrResultados == null || arrResultados.Count == 0) && !blnSemAlbum)
                {
                    string strUrlPorTitulo = string.Format(
                        "{0}/database/search?release_title={1}&type=release",
                        STR_API_BASE,
                        Uri.EscapeDataString(strAlbumLimpo)
                    );
                    strJsonBusca = ExecutarRequisicaoJson(strUrlPorTitulo, strToken);
                    if (!string.IsNullOrEmpty(strJsonBusca))
                    {
                        Dictionary<string, object> dicBuscaTitulo = jssSerializer.Deserialize<Dictionary<string, object>>(strJsonBusca);
                        if (dicBuscaTitulo != null && dicBuscaTitulo.ContainsKey("results"))
                        {
                            arrResultados = dicBuscaTitulo["results"] as ArrayList;
                        }
                    }
                }

                // Tentativa 3: Busca ampla com termo geral.
                // Se for coletânea, busca exclusivamente pelo título do álbum para evitar o mar de falsos positivos de "Various".
                if (arrResultados == null || arrResultados.Count == 0)
                {
                    string strTermoAmplo = blnArtistaBuscaColetanea && !blnSemAlbum
                        ? strAlbumLimpo : (strArtistaLimpo + " " + strAlbumLimpo).Trim();
                    string strUrlAmpla = string.Format(
                        "{0}/database/search?q={1}&type=release",
                        STR_API_BASE,
                        Uri.EscapeDataString(strTermoAmplo)
                    );
                    strJsonBusca = ExecutarRequisicaoJson(strUrlAmpla, strToken);
                    if (!string.IsNullOrEmpty(strJsonBusca))
                    {
                        Dictionary<string, object> dicBuscaAmpla = jssSerializer.Deserialize<Dictionary<string, object>>(strJsonBusca);
                        if (dicBuscaAmpla != null && dicBuscaAmpla.ContainsKey("results"))
                        {
                            arrResultados = dicBuscaAmpla["results"] as ArrayList;
                        }
                    }
                }

                // Tentativa 4: Busca pela discografia do artista (resiliência contra títulos transliterados ou variações de grafia)
                if ((arrResultados == null || arrResultados.Count == 0) && !blnArtistaBuscaColetanea && !string.IsNullOrEmpty(strArtistaLimpo))
                {
                    actLog(string.Format("Discogs: Nenhuma correspondência direta por título. Consultando lançamentos do artista '{0}'...", strArtistaLimpo));
                    string strUrlArtista = string.Format(
                        "{0}/database/search?artist={1}&type=release",
                        STR_API_BASE,
                        Uri.EscapeDataString(strArtistaLimpo)
                    );
                    strJsonBusca = ExecutarRequisicaoJson(strUrlArtista, strToken);
                    if (!string.IsNullOrEmpty(strJsonBusca))
                    {
                        Dictionary<string, object> dicBuscaArtista = jssSerializer.Deserialize<Dictionary<string, object>>(strJsonBusca);
                        if (dicBuscaArtista != null && dicBuscaArtista.ContainsKey("results"))
                        {
                            arrResultados = dicBuscaArtista["results"] as ArrayList;
                        }
                    }
                }

                if (arrResultados == null || arrResultados.Count == 0)
                {
                    actLog(string.Format("Discogs: Nenhum lançamento encontrado para '{0} - {1}'.", strArtistaLimpo, strAlbumLimpo));
                    return null;
                }

                actLog(string.Format("Discogs: {0} lançamento(s) candidato(s) encontrado(s). Analisando compatibilidade e faixas...", arrResultados.Count));

                // Analisa os primeiros candidatos para eleger o melhor lançamento com trava de similaridade
                TDiscogsReleaseInfo objMelhorCandidato = null;
                double dblMelhorScore = 0.0;
                int intLimiteAnalise = Math.Min(arrResultados.Count, 5);

                for (int i = 0; i < intLimiteAnalise; i++)
                {
                    Dictionary<string, object> dicItem = arrResultados[i] as Dictionary<string, object>;
                    if (dicItem == null || !dicItem.ContainsKey("resource_url")) continue;

                    string strResourceUrl = dicItem["resource_url"].ToString();
                    TDiscogsReleaseInfo objInfo = ObterDetalhesDoLancamento(strResourceUrl, strToken, actLog);
                    if (objInfo == null) continue;

                    // 1. Validação de Similaridade do Título do Álbum (sem álbum
                    //    informado, não há com o que comparar: decide a contagem de faixas)
                    double dblSimilaridadeAlbum = blnSemAlbum ? 0.5 : CalcularSimilaridadeTexto(strAlbumLimpo, objInfo.strAlbum);
                    if (!blnSemAlbum && dblSimilaridadeAlbum < 0.60)
                    {
                        actLog(string.Format("Discogs: Candidato '{0} - {1}' descartado por divergência de título com '{2}' (similaridade {3:F2} < 0.60).",
                            objInfo.strArtist ?? "Desconhecido", objInfo.strAlbum ?? "Desconhecido", strAlbumLimpo, dblSimilaridadeAlbum));
                        continue;
                    }

                    // 2. Validação de Similaridade do Artista (quando não for busca de coletânea "Various")
                    if (!blnArtistaBuscaColetanea && !blnSemArtista)
                    {
                        double dblSimilaridadeArtista = CalcularSimilaridadeTexto(strArtistaLimpo, objInfo.strArtist);
                        if (dblSimilaridadeArtista < 0.50)
                        {
                            actLog(string.Format("Discogs: Candidato '{0} - {1}' descartado por divergência de artista com '{2}' (similaridade {3:F2} < 0.50).",
                                objInfo.strArtist ?? "Desconhecido", objInfo.strAlbum ?? "Desconhecido", strArtistaLimpo, dblSimilaridadeArtista));
                            continue;
                        }
                    }

                    // Calcula score composto considerando similaridade e contagem exata de faixas
                    bool blnFaixasExatas = (intQtdFaixasEsperadas > 0 && objInfo.lstTrackTitles.Count == intQtdFaixasEsperadas);
                    double dblScoreCandidato = dblSimilaridadeAlbum + (blnFaixasExatas ? 1.0 : 0.0);

                    // Se a contagem de faixas coincidir perfeitamente E o título for altamente compatível, temos a prensagem exata
                    if (blnFaixasExatas)
                    {
                        actLog(string.Format("Discogs: Correspondência exata de faixas ({0}) e título compatível ('{1}', similaridade {2:F2}). Prensagem selecionada.",
                            intQtdFaixasEsperadas, objInfo.strAlbum, dblSimilaridadeAlbum));
                        objMelhorCandidato = objInfo;
                        break;
                    }

                    if (objMelhorCandidato == null || dblScoreCandidato > dblMelhorScore)
                    {
                        objMelhorCandidato = objInfo;
                        dblMelhorScore = dblScoreCandidato;
                    }
                }

                if (objMelhorCandidato == null)
                {
                    actLog(string.Format("Discogs: Nenhum candidato atingiu os critérios mínimos de similaridade para '{0} - {1}'. Mantendo metadados originais.", strArtistaLimpo, strAlbumLimpo));
                    return null;
                }

                // Baixa a imagem da capa em alta resolução caso disponível
                if (!string.IsNullOrEmpty(objMelhorCandidato.strCoverUrl))
                {
                    actLog("Discogs: Baixando capa oficial autenticada...");
                    try
                    {
                        objMelhorCandidato.arrCoverBytes = BaixarCapaAutenticada(objMelhorCandidato.strCoverUrl, strToken);
                        if (objMelhorCandidato.arrCoverBytes != null)
                        {
                            actLog(string.Format("Discogs: Capa baixada com sucesso ({0:N0} bytes).", objMelhorCandidato.arrCoverBytes.Length));
                        }
                    }
                    catch (Exception exCapa)
                    {
                        actLog("Discogs: Aviso ao baixar capa: " + exCapa.Message);
                    }
                }

                return objMelhorCandidato;
            }
            catch (Exception exGeral)
            {
                actLog("Discogs: Falha na consulta de metadados: " + exGeral.Message);
                return null;
            }
        }

        /// <summary>
        /// Obtém o JSON detalhado de um release específico (/releases/{id}) e extrai a tracklist oficial e estilos.
        /// </summary>
        private static TDiscogsReleaseInfo ObterDetalhesDoLancamento(string strResourceUrl, string strToken, Action<string> actLog)
        {
            try
            {
                string strJsonDetalhe = ExecutarRequisicaoJson(strResourceUrl, strToken);
                if (string.IsNullOrEmpty(strJsonDetalhe)) return null;

                JavaScriptSerializer jss = new JavaScriptSerializer();
                Dictionary<string, object> dicDetalhe = jss.Deserialize<Dictionary<string, object>>(strJsonDetalhe);
                if (dicDetalhe == null) return null;

                TDiscogsReleaseInfo objRelease = new TDiscogsReleaseInfo();

                if (dicDetalhe.ContainsKey("id"))
                {
                    int intId;
                    if (int.TryParse(dicDetalhe["id"].ToString(), out intId))
                        objRelease.intReleaseId = intId;
                }

                // Título do Álbum
                if (dicDetalhe.ContainsKey("title"))
                {
                    objRelease.strAlbum = dicDetalhe["title"].ToString();
                }

                // Artista
                if (dicDetalhe.ContainsKey("artists"))
                {
                    ArrayList arrArtistas = dicDetalhe["artists"] as ArrayList;
                    if (arrArtistas != null && arrArtistas.Count > 0)
                    {
                        Dictionary<string, object> dicPrimeiroArtista = arrArtistas[0] as Dictionary<string, object>;
                        if (dicPrimeiroArtista != null && dicPrimeiroArtista.ContainsKey("name"))
                        {
                            string strNomeBruto = dicPrimeiroArtista["name"].ToString();
                            // Discogs usa notação como "Artista (2)" para desambiguação; removemos o sufixo numérico
                            objRelease.strArtist = Regex.Replace(strNomeBruto, @"\s+\(\d+\)$", "").Trim();
                        }
                    }
                }

                // Gravadora + catálogo, e país: o que separa uma prensagem da outra
                if (dicDetalhe.ContainsKey("labels"))
                {
                    ArrayList arrSelos = dicDetalhe["labels"] as ArrayList;
                    Dictionary<string, object> dicSelo = arrSelos != null && arrSelos.Count > 0
                        ? arrSelos[0] as Dictionary<string, object> : null;
                    if (dicSelo != null && dicSelo.ContainsKey("name"))
                    {
                        string strSelo = Regex.Replace(dicSelo["name"].ToString(), @"\s+\(\d+\)$", "").Trim();
                        string strCatalogo = dicSelo.ContainsKey("catno") && dicSelo["catno"] != null ? dicSelo["catno"].ToString().Trim() : "";
                        if (strCatalogo.Equals("none", StringComparison.OrdinalIgnoreCase)) strCatalogo = "";
                        objRelease.strGravadora = (strSelo + " " + strCatalogo).Trim();
                    }
                }
                if (dicDetalhe.ContainsKey("country") && dicDetalhe["country"] != null)
                {
                    objRelease.strPais = dicDetalhe["country"].ToString();
                }

                // Ano de Lançamento
                if (dicDetalhe.ContainsKey("year"))
                {
                    string strAno = dicDetalhe["year"].ToString();
                    if (strAno != "0" && !string.IsNullOrEmpty(strAno))
                    {
                        objRelease.strYear = strAno;
                    }
                }

                // Gêneros
                if (dicDetalhe.ContainsKey("genres"))
                {
                    ArrayList arrGeneros = dicDetalhe["genres"] as ArrayList;
                    if (arrGeneros != null)
                    {
                        foreach (object objG in arrGeneros)
                        {
                            if (objG != null) objRelease.lstGenres.Add(objG.ToString());
                        }
                    }
                }

                // Estilos Refinados (o ponto forte do Discogs: ex: MPB, Samba-Canção, Ambient, etc.)
                if (dicDetalhe.ContainsKey("styles"))
                {
                    ArrayList arrEstilos = dicDetalhe["styles"] as ArrayList;
                    if (arrEstilos != null)
                    {
                        foreach (object objE in arrEstilos)
                        {
                            if (objE != null) objRelease.lstStyles.Add(objE.ToString());
                        }
                    }
                }

                // Tracklist oficial
                if (dicDetalhe.ContainsKey("tracklist"))
                {
                    ArrayList arrTracklist = dicDetalhe["tracklist"] as ArrayList;
                    if (arrTracklist != null)
                    {
                        foreach (object objT in arrTracklist)
                        {
                            Dictionary<string, object> dicTrack = objT as Dictionary<string, object>;
                            if (dicTrack == null) continue;

                            // Ignora cabeçalhos ou divisões de atos
                            if (dicTrack.ContainsKey("type_") && dicTrack["type_"].ToString().Equals("heading", StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (dicTrack.ContainsKey("title"))
                            {
                                string strTituloFaixa = dicTrack["title"].ToString();
                                if (!string.IsNullOrEmpty(strTituloFaixa))
                                {
                                    objRelease.lstTrackTitles.Add(strTituloFaixa);

                                    // Extrai o artista específico desta faixa (crucial para Coletâneas / Various Artists)
                                    string strArtistaFaixa = null;
                                    if (dicTrack.ContainsKey("artists"))
                                    {
                                        ArrayList arrArtistasFaixa = dicTrack["artists"] as ArrayList;
                                        if (arrArtistasFaixa != null && arrArtistasFaixa.Count > 0)
                                        {
                                            List<string> lstNomes = new List<string>();
                                            foreach (object objA in arrArtistasFaixa)
                                            {
                                                Dictionary<string, object> dicA = objA as Dictionary<string, object>;
                                                if (dicA != null && dicA.ContainsKey("name"))
                                                {
                                                    string strNome = Regex.Replace(dicA["name"].ToString(), @"\s+\(\d+\)$", "").Trim();
                                                    if (!string.IsNullOrEmpty(strNome)) lstNomes.Add(strNome);
                                                }
                                            }
                                            if (lstNomes.Count > 0)
                                            {
                                                strArtistaFaixa = string.Join(", ", lstNomes.ToArray());
                                            }
                                        }
                                    }

                                    // Créditos da faixa: compositor e, em coletânea, os intérpretes.
                                    // Em coletânea de música clássica a faixa costuma não ter
                                    // "artists" — quem toca vem em "extraartists" (orquestra,
                                    // regente, solista), junto com o compositor.
                                    string strCompositor;
                                    string strInterpretes;
                                    LerCreditosFaixa(dicTrack, out strCompositor, out strInterpretes);
                                    objRelease.lstTrackComposers.Add(strCompositor ?? "");
                                    objRelease.lstTrackDurations.Add(
                                        LerDuracao(dicTrack.ContainsKey("duration") && dicTrack["duration"] != null
                                                   ? dicTrack["duration"].ToString() : null));

                                    if (string.IsNullOrEmpty(strArtistaFaixa) && EhColetanea(objRelease.strArtist)
                                        && !string.IsNullOrEmpty(strInterpretes))
                                    {
                                        strArtistaFaixa = strInterpretes;
                                    }

                                    if (string.IsNullOrEmpty(strArtistaFaixa))
                                    {
                                        strArtistaFaixa = objRelease.strArtist ?? "Coletânea";
                                    }

                                    objRelease.lstTrackArtists.Add(strArtistaFaixa);
                                }
                            }
                        }
                    }
                }

                // Imagem de Capa Principal
                if (dicDetalhe.ContainsKey("images"))
                {
                    ArrayList arrImagens = dicDetalhe["images"] as ArrayList;
                    if (arrImagens != null && arrImagens.Count > 0)
                    {
                        string strCandidataSecundaria = null;
                        foreach (object objImg in arrImagens)
                        {
                            Dictionary<string, object> dicImg = objImg as Dictionary<string, object>;
                            if (dicImg == null) continue;

                            string strUri = dicImg.ContainsKey("uri") ? dicImg["uri"].ToString() : null;
                            string strTipo = dicImg.ContainsKey("type") ? dicImg["type"].ToString() : null;

                            if (!string.IsNullOrEmpty(strUri))
                            {
                                if (strTipo != null && strTipo.Equals("primary", StringComparison.OrdinalIgnoreCase))
                                {
                                    objRelease.strCoverUrl = strUri;
                                    break;
                                }
                                if (strCandidataSecundaria == null)
                                {
                                    strCandidataSecundaria = strUri;
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(objRelease.strCoverUrl) && !string.IsNullOrEmpty(strCandidataSecundaria))
                        {
                            objRelease.strCoverUrl = strCandidataSecundaria;
                        }
                    }
                }

                return objRelease;
            }
            catch (Exception ex)
            {
                actLog("Discogs: Falha ao obter detalhes do lançamento: " + ex.Message);
                return null;
            }
        }

        // ===============================================================
        //  LINK EXATO
        //
        //  Quando o usuário cola o link de um lançamento, ele já fez a
        //  identificação: o ARCA só tem de ler aquele lançamento, não buscar
        //  outro parecido. Por isso este caminho não pontua candidatos nem
        //  compara títulos — vai direto ao /releases/{id}.
        // ===============================================================

        private static readonly Regex REG_LINK_DISCOGS = new Regex(
            @"discogs\.com/(?:[\w-]+/)?(release|master)/(\d+)", RegexOptions.IgnoreCase);

        /// <summary>
        /// O texto é um link de lançamento (/release/123) ou de master
        /// (/master/123) do Discogs? Aceita o prefixo de idioma do site
        /// (/pt_BR/release/…) e o lixo de rastreamento depois do número.
        /// </summary>
        public static bool EhLink(string strEntrada)
        {
            return !string.IsNullOrEmpty(strEntrada) && REG_LINK_DISCOGS.IsMatch(strEntrada);
        }

        /// <summary>
        /// Lê exatamente o lançamento do link, com faixas, créditos e capa.
        /// Link de master vai ao lançamento principal dele. Lança exceção se
        /// não conseguir: um link explícito que falha não pode cair calado
        /// numa busca por texto, que traria outro disco.
        /// </summary>
        public static TDiscogsReleaseInfo ConsultarPorLink(string strEntrada, Action<string> actLog)
        {
            if (actLog == null) actLog = delegate { };

            Match objLink = REG_LINK_DISCOGS.Match(strEntrada ?? "");
            if (!objLink.Success) throw new ArgumentException("não é um link de lançamento do Discogs.");

            // TLS 1.2 e certificado do proxy: sem isto, se o Discogs for a
            // primeira chamada HTTPS do processo, o handshake falha.
            NetworkConfiguration.Initialize();
            string strToken = ObterTokenConfigurado();

            bool blnMaster = objLink.Groups[1].Value.Equals("master", StringComparison.OrdinalIgnoreCase);
            string strId = objLink.Groups[2].Value;

            if (blnMaster)
            {
                actLog(string.Format("Discogs: link de master {0}; buscando o lançamento principal...", strId));
                string strJsonMaster = ExecutarRequisicaoJson(STR_API_BASE + "/masters/" + strId, strToken);
                Dictionary<string, object> dicMaster = string.IsNullOrEmpty(strJsonMaster) ? null
                    : new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(strJsonMaster);
                if (dicMaster == null || !dicMaster.ContainsKey("main_release") || dicMaster["main_release"] == null)
                    throw new InvalidOperationException("o master " + strId + " do Discogs não indica lançamento principal.");
                strId = dicMaster["main_release"].ToString();
            }

            actLog(string.Format("Discogs: lendo o lançamento {0} indicado pelo link...", strId));
            TDiscogsReleaseInfo objRelease = ObterDetalhesDoLancamento(STR_API_BASE + "/releases/" + strId, strToken, actLog);
            if (objRelease == null || objRelease.lstTrackTitles.Count == 0)
                throw new InvalidOperationException("não consegui ler o lançamento " + strId + " do Discogs (veja o registro).");

            if (!string.IsNullOrEmpty(objRelease.strCoverUrl))
            {
                try { objRelease.arrCoverBytes = BaixarCapaAutenticada(objRelease.strCoverUrl, strToken); }
                catch (Exception ex) { actLog("Discogs: falha ao baixar a capa: " + ex.Message); }
            }
            if (objRelease.arrCoverBytes == null)
                actLog("Discogs: o lançamento não tem capa disponível.");

            actLog(string.Format("Discogs: {0} — {1}, {2} faixas.",
                objRelease.strArtist, objRelease.strAlbum, objRelease.lstTrackTitles.Count));
            return objRelease;
        }

        /// <summary>
        /// "9:48" ou "1:02:15" em segundos; -1 quando vazio ou ilegível (o
        /// Discogs deixa a duração em branco em muitos lançamentos).
        /// </summary>
        private static int LerDuracao(string strDuracao)
        {
            if (string.IsNullOrEmpty(strDuracao)) return -1;
            string[] arrPartes = strDuracao.Trim().Split(':');
            int intTotal = 0;
            foreach (string strParte in arrPartes)
            {
                int intValor;
                if (!int.TryParse(strParte, out intValor)) return -1;
                intTotal = intTotal * 60 + intValor;
            }
            return intTotal > 0 ? intTotal : -1;
        }

        /// <summary>
        /// "Various" e equivalentes: o disco não tem um artista só, e o artista
        /// de cada faixa tem de vir dos créditos dela.
        /// </summary>
        private static bool EhColetanea(string strArtista)
        {
            if (string.IsNullOrEmpty(strArtista)) return false;
            string strNorm = NormalizarParaComparacao(strArtista);
            return strNorm.StartsWith("various") || strNorm.StartsWith("vario")
                   || strNorm.StartsWith("diversos") || strNorm.StartsWith("coletanea");
        }

        /// <summary>
        /// Créditos de uma faixa em "extraartists": quem compôs, e quem
        /// interpreta (orquestra, regente, solista, grupo). Papéis técnicos e
        /// de produção ficam de fora. Nomes canônicos do Discogs, sem o
        /// sufixo de desambiguação "(2)".
        /// </summary>
        private static void LerCreditosFaixa(Dictionary<string, object> dicTrack,
                                             out string strCompositor, out string strInterpretes)
        {
            strCompositor = null;
            strInterpretes = null;
            if (!dicTrack.ContainsKey("extraartists")) return;
            ArrayList arrCreditos = dicTrack["extraartists"] as ArrayList;
            if (arrCreditos == null) return;

            List<string> lstCompositores = new List<string>();
            List<string> lstInterpretes = new List<string>();
            foreach (object objC in arrCreditos)
            {
                Dictionary<string, object> dicC = objC as Dictionary<string, object>;
                if (dicC == null || !dicC.ContainsKey("name")) continue;

                string strNome = Regex.Replace(dicC["name"].ToString(), @"\s+\(\d+\)$", "").Trim();
                string strPapel = dicC.ContainsKey("role") && dicC["role"] != null ? dicC["role"].ToString() : "";
                if (string.IsNullOrEmpty(strNome)) continue;

                if (Regex.IsMatch(strPapel, @"Composed|Written|Music By|Songwriter", RegexOptions.IgnoreCase))
                {
                    if (!lstCompositores.Contains(strNome)) lstCompositores.Add(strNome);
                }
                else if (!Regex.IsMatch(strPapel,
                    @"Lyrics|Arranged|Produc|Engineer|Master|Mix|Record|Design|Photo|Liner|Artwork|Edit|Translat|Publish|Copyright|Licens|Executive",
                    RegexOptions.IgnoreCase))
                {
                    if (!lstInterpretes.Contains(strNome)) lstInterpretes.Add(strNome);
                }
            }

            if (lstCompositores.Count > 0) strCompositor = string.Join(", ", lstCompositores.ToArray());
            if (lstInterpretes.Count > 0) strInterpretes = string.Join(", ", lstInterpretes.ToArray());
        }

        /// <summary>
        /// Baixa a imagem de capa do Discogs fornecendo os cabeçalhos de autenticação obrigatórios.
        /// Valida o cabeçalho JPEG para evitar arquivos corrompidos.
        /// </summary>
        public static byte[] BaixarCapaAutenticada(string strUrlImagem, string strToken)
        {
            if (string.IsNullOrEmpty(strUrlImagem)) return null;

            HttpWebRequest reqImagem = (HttpWebRequest)WebRequest.Create(strUrlImagem);
            reqImagem.Proxy = NetworkConfiguration.CreateProxy();
            reqImagem.UserAgent = STR_USER_AGENT;
            if (!string.IsNullOrEmpty(strToken))
            {
                reqImagem.Headers.Add("Authorization", "Discogs token=" + strToken);
            }
            reqImagem.Timeout = 20000; // 20s
            reqImagem.ReadWriteTimeout = 20000;
            reqImagem.AllowAutoRedirect = true;
            reqImagem.MaximumAutomaticRedirections = 4;

            using (HttpWebResponse resp = NetworkConfiguration.Responder(reqImagem))
            {
                if (resp.StatusCode != HttpStatusCode.OK)
                {
                    throw new WebException(string.Format("Status HTTP inválido ao baixar capa: {0}", resp.StatusCode));
                }

                // Teto de 8 MB também para resposta sem tamanho declarado.
                byte[] arrBytes = NetworkConfiguration.LerCorpo(resp, 8 * 1024 * 1024);
                {

                    // Validação de integridade do cabeçalho JPEG (FF D8 FF) ou PNG (89 50 4E 47)
                    if (arrBytes.Length >= 3 && arrBytes[0] == 0xFF && arrBytes[1] == 0xD8 && arrBytes[2] == 0xFF)
                    {
                        return arrBytes;
                    }

                    if (arrBytes.Length >= 8 && arrBytes[0] == 0x89 && arrBytes[1] == 0x50 && arrBytes[2] == 0x4E && arrBytes[3] == 0x47)
                    {
                        return arrBytes;
                    }

                    // Sem assinatura de imagem, não é imagem — mesmo que seja grande:
                    // uma página HTML de bloqueio passava por "capa" pelo tamanho.
                    throw new InvalidDataException("Arquivo de imagem retornado não possui cabeçalho de imagem válido.");
                }
            }
        }

        /// <summary>
        /// Executa requisição GET autenticada na API do Discogs e retorna o corpo como string.
        /// </summary>
        private static string ExecutarRequisicaoJson(string strUrl, string strToken)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(strUrl);
            req.Proxy = NetworkConfiguration.CreateProxy();
            req.UserAgent = STR_USER_AGENT;
            if (!string.IsNullOrEmpty(strToken))
            {
                req.Headers.Add("Authorization", "Discogs token=" + strToken);
            }
            req.Timeout = 15000;
            req.ReadWriteTimeout = 15000;
            req.AllowAutoRedirect = true;
            req.MaximumAutomaticRedirections = 3;

            using (HttpWebResponse resp = NetworkConfiguration.Responder(req))
            {
                if (resp.StatusCode != HttpStatusCode.OK) return null;
                using (Stream stream = resp.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, System.Text.Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        /// <summary>
        /// Normaliza termos de busca removendo anotações de edições, discos múltiplos,
        /// caracteres especiais e anotações/gravadoras entre parênteses no final.
        /// </summary>
        private static string LimparTermoDeBusca(string strTexto)
        {
            if (string.IsNullOrEmpty(strTexto)) return string.Empty;
            string strResultado = strTexto;
            // Remove "(Disc 1)", "[Live]", "(Remastered)", etc.
            strResultado = Regex.Replace(strResultado, @"\s*[\(\[][^\]\)]*(disc|cd|live|remaster|deluxe|edition|vers)[^\]\)]*[\)\]]", "", RegexOptions.IgnoreCase);
            // Remove anotações e selos entre parênteses no final do título (exceto termos legítimos como "Ao Vivo", "Live", "Acústico")
            strResultado = Regex.Replace(strResultado, @"\s*\((?!(?:ao vivo|live|acustico|acoustic|remastered|deluxe)\b)[^\)]+\)\s*$", "", RegexOptions.IgnoreCase);
            strResultado = Regex.Replace(strResultado, @"\s+", " ").Trim();
            return strResultado;
        }

        /// <summary>
        /// Remove acentos gráficos de uma cadeia de caracteres para comparações textuais robustas.
        /// </summary>
        private static string RemoverAcentos(string strTexto)
        {
            if (string.IsNullOrEmpty(strTexto)) return string.Empty;
            string strNormalizada = strTexto.Normalize(System.Text.NormalizationForm.FormD);
            System.Text.StringBuilder sbResultado = new System.Text.StringBuilder();
            foreach (char chrC in strNormalizada)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(chrC) != System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    sbResultado.Append(chrC);
                }
            }
            return sbResultado.ToString().Normalize(System.Text.NormalizationForm.FormC);
        }

        private static readonly HashSet<string> SET_STOPWORDS = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "o", "a", "os", "as", "um", "uma", "uns", "umas",
            "de", "do", "da", "dos", "das", "em", "no", "na", "nos", "nas",
            "e", "ou", "para", "por", "com",
            "the", "a", "an", "of", "in", "on", "at", "to", "for", "with", "and", "or"
        };

        /// <summary>
        /// Calcula a similaridade semântica entre duas cadeias textuais (retorna valor entre 0.0 e 1.0).
        /// Avalia contenção direta, remoção de stopwords funcionais, cobertura (recall) de palavras-chave
        /// e índice de Jaccard, garantindo que termos nucleares de álbuns e artistas coincidam.
        /// </summary>
        public static double CalcularSimilaridadeTexto(string strTextoA, string strTextoB)
        {
            if (string.IsNullOrEmpty(strTextoA) && string.IsNullOrEmpty(strTextoB)) return 1.0;
            if (string.IsNullOrEmpty(strTextoA) || string.IsNullOrEmpty(strTextoB)) return 0.0;

            string strNormA = NormalizarParaComparacao(strTextoA);
            string strNormB = NormalizarParaComparacao(strTextoB);

            if (string.Equals(strNormA, strNormB, StringComparison.OrdinalIgnoreCase)) return 1.0;
            if (strNormA.Length == 0 || strNormB.Length == 0) return 0.0;

            // Contenção direta de substring caso uma englobe a outra
            if (strNormA.Contains(strNormB) || strNormB.Contains(strNormA))
            {
                double dblRatio = (double)Math.Min(strNormA.Length, strNormB.Length) / Math.Max(strNormA.Length, strNormB.Length);
                if (dblRatio >= 0.70)
                {
                    return Math.Max(0.85, dblRatio);
                }
            }

            List<string> lstPalavrasA = ExtrairPalavrasChave(strNormA);
            List<string> lstPalavrasB = ExtrairPalavrasChave(strNormB);

            if (lstPalavrasA.Count == 0 || lstPalavrasB.Count == 0) return 0.0;

            HashSet<string> setA = new HashSet<string>(lstPalavrasA, StringComparer.OrdinalIgnoreCase);
            HashSet<string> setB = new HashSet<string>(lstPalavrasB, StringComparer.OrdinalIgnoreCase);

            HashSet<string> setIntersecao = new HashSet<string>(setA, StringComparer.OrdinalIgnoreCase);
            setIntersecao.IntersectWith(setB);

            // Cobertura (recall) do termo pesquisado (A) em relação ao candidato (B)
            double dblCoberturaAlvo = (double)setIntersecao.Count / setA.Count;

            // Jaccard das palavras-chave
            HashSet<string> setUniao = new HashSet<string>(setA, StringComparer.OrdinalIgnoreCase);
            setUniao.UnionWith(setB);
            double dblJaccard = setUniao.Count > 0 ? (double)setIntersecao.Count / setUniao.Count : 0.0;

            // Se o candidato não cobrir ao menos 70% das palavras-chave essenciais do termo alvo, rejeita
            if (dblCoberturaAlvo < 0.70)
            {
                return Math.Min(dblCoberturaAlvo, dblJaccard);
            }

            return (dblCoberturaAlvo * 0.6) + (dblJaccard * 0.4);
        }

        /// <summary>
        /// Extrai tokens de palavras ignorando stopwords funcionais comuns que causam ruído na correspondência.
        /// </summary>
        private static List<string> ExtrairPalavrasChave(string strTexto)
        {
            string[] arrTokens = strTexto.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> lstResultado = new List<string>();
            foreach (string strT in arrTokens)
            {
                if (!SET_STOPWORDS.Contains(strT))
                {
                    lstResultado.Add(strT);
                }
            }
            if (lstResultado.Count == 0)
            {
                lstResultado.AddRange(arrTokens);
            }
            return lstResultado;
        }

        /// <summary>
        /// Normaliza uma cadeia textual para fins de comparação semântica:
        /// remove anotações entre colchetes/parênteses, remove acentos gráficos e caracteres não alfanuméricos.
        /// </summary>
        public static string NormalizarParaComparacao(string strTexto)
        {
            if (string.IsNullOrEmpty(strTexto)) return string.Empty;

            // Remove anotações como (Disc 1), [Live], etc.
            string strLimpo = Regex.Replace(strTexto, @"\s*[\(\[][^\]\)]*[\)\]]", " ");

            // Remove acentos
            string strSemAcento = RemoverAcentos(strLimpo);

            // Converte pontuações para espaços e passa para minúsculas
            string strAlfanumerico = Regex.Replace(strSemAcento, @"[^\w\s]", " ").ToLowerInvariant();

            // Condensa espaços consecutivos
            strAlfanumerico = Regex.Replace(strAlfanumerico, @"\s+", " ").Trim();

            return strAlfanumerico;
        }
    }
}
