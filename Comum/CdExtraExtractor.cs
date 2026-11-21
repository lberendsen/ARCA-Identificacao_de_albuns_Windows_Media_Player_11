using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace ARCA.Modelo
{
    /// <summary>
    /// Representa um arquivo extraído da sessão de dados de um Enhanced CD (CD Extra / Blue Book).
    /// </summary>
    public class TCdExtraFile
    {
        public string strNomeArquivo { get; set; }
        public string strCaminhoCompleto { get; set; }
        public long lngTamanhoBytes { get; set; }
        public string strSha256 { get; set; }
    }

    /// <summary>
    /// Encapsula os metadados enriquecidos e a capa oficial de alta resolução minerados
    /// a partir da sessão de dados de um Enhanced CD / Copy Control.
    /// </summary>
    public class TMetadadosCdExtra
    {
        public string strAlbum { get; set; }
        public string strArtistaAlbum { get; set; }
        public string strAno { get; set; }
        public List<string> lstTitulosFaixas { get; set; }
        public List<string> lstArtistasFaixas { get; set; }
        public List<string> lstCompositoresFaixas { get; set; }
        public byte[] arrCapaBytes { get; set; }
        public string strCaminhoCapaOriginal { get; set; }
        public int intLarguraCapa { get; set; }
        public int intAlturaCapa { get; set; }

        public TMetadadosCdExtra()
        {
            lstTitulosFaixas = new List<string>();
            lstArtistasFaixas = new List<string>();
            lstCompositoresFaixas = new List<string>();
        }
    }

    /// <summary>
    /// Responsável exclusivamente pela interpretação do sistema de arquivos ISO 9660 / Joliet
    /// contido nos setores da sessão de dados de um Enhanced CD (CD Extra / Blue Book)
    /// e pela extração direta de arquivos de vídeo e multimídia sem necessidade de ferramentas externas.
    /// Respeita o Princípio de Responsabilidade Única (SRP) e o desacoplamento da interface.
    /// </summary>
    public static class TCdExtraExtractor
    {
        private static readonly string[] arrExtensoesVideo = { ".mpg", ".mpeg", ".avi", ".mov", ".wmv", ".mp4", ".mkv", ".m4v" };

        /// <summary>
        /// Copia os vídeos da sessão de dados de um CD Extra a partir da
        /// unidade montada (strRaiz, como "E:\").
        ///
        /// Antes isto se tentava lendo o ISO 9660 de dentro do AudioCD.bin —
        /// que nunca funcionou: o .bin termina no fim do áudio, e a sessão de
        /// dados não está nele. O Windows já monta essa sessão como sistema de
        /// arquivos (com os nomes longos do Joliet e os acentos); ler por ele
        /// dispensa reimplementar o ISO sobre setor cru.
        ///
        /// Cada vídeo é conferido: o SHA-256 da cópia tem de ser igual ao de
        /// uma segunda leitura do disco. Arquivos de mesmo nome em pastas
        /// diferentes não se sobrescrevem — o segundo leva o nome da pasta.
        /// </summary>
        public static List<TCdExtraFile> CopiarVideosDaUnidade(string strRaiz, string strPastaDestino, Action<string> actLog)
        {
            List<TCdExtraFile> lstCopiados = new List<TCdExtraFile>();
            if (actLog == null) actLog = delegate { };
            if (string.IsNullOrEmpty(strRaiz) || !Directory.Exists(strRaiz))
            {
                actLog("CD Extra: a unidade não está acessível; os vídeos não foram copiados.");
                return lstCopiados;
            }

            List<string> lstVideos = new List<string>();
            Queue<string> filaPastas = new Queue<string>();
            filaPastas.Enqueue(strRaiz);
            int intPastas = 0;
            while (filaPastas.Count > 0 && intPastas++ < 2000)
            {
                string strPasta = filaPastas.Dequeue();
                try
                {
                    foreach (string f in Directory.GetFiles(strPasta))
                        if (Array.IndexOf(arrExtensoesVideo, Path.GetExtension(f).ToLowerInvariant()) >= 0) lstVideos.Add(f);
                    foreach (string d in Directory.GetDirectories(strPasta)) filaPastas.Enqueue(d);
                }
                catch (Exception ex) { actLog("CD Extra: não consegui ler a pasta " + strPasta + ": " + ex.Message); }
            }
            if (lstVideos.Count == 0)
            {
                actLog("CD Extra: nenhum vídeo na sessão de dados.");
                return lstCopiados;
            }
            lstVideos.Sort(StringComparer.OrdinalIgnoreCase);
            actLog(string.Format("CD Extra: {0} vídeo(s) na sessão de dados. Copiando e conferindo cada um com uma segunda leitura do disco...", lstVideos.Count));

            HashSet<string> setNomes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string strOrigem in lstVideos)
            {
                string strNome = Path.GetFileName(strOrigem);
                if (setNomes.Contains(strNome))
                {
                    string strPai = Path.GetFileName(Path.GetDirectoryName(strOrigem));
                    strNome = (string.IsNullOrEmpty(strPai) ? "disco" : strPai) + " - " + strNome;
                }
                for (int n = 2; setNomes.Contains(strNome); n++)
                    strNome = Path.GetFileNameWithoutExtension(strOrigem) + " (" + n + ")" + Path.GetExtension(strOrigem);
                setNomes.Add(strNome);
                string strDestino = Path.Combine(strPastaDestino, strNome);

                try
                {
                    string strHashCopia;
                    using (SHA256 sha = SHA256.Create())
                    using (FileStream fsOrigem = new FileStream(strOrigem, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                    using (FileStream fsDestino = new FileStream(strDestino, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    {
                        byte[] buf = new byte[1 << 20];
                        int r;
                        while ((r = fsOrigem.Read(buf, 0, buf.Length)) > 0)
                        {
                            fsDestino.Write(buf, 0, r);
                            sha.TransformBlock(buf, 0, r, null, 0);
                        }
                        sha.TransformFinalBlock(new byte[0], 0, 0);
                        fsDestino.Flush(true);
                        strHashCopia = Hex(sha.Hash);
                    }
                    // A cópia no disco de destino e uma segunda leitura do CD têm de
                    // dar o mesmo número que a primeira.
                    string strHashGravado = HashDe(strDestino);
                    string strHashRelido = HashDe(strOrigem);
                    if (strHashGravado != strHashCopia || strHashRelido != strHashCopia)
                    {
                        actLog(string.Format("⚠ CD Extra: '{0}' NÃO confere ({1}). A cópia foi descartada.", strNome,
                            strHashRelido != strHashCopia ? "duas leituras do disco deram resultados diferentes" : "a cópia gravada difere do que foi lido"));
                        try { File.Delete(strDestino); } catch { }
                        continue;
                    }
                    long lngTamanho = new FileInfo(strDestino).Length;
                    actLog(string.Format("CD Extra: '{0}' copiado e conferido ({1:F2} MB, SHA-256 {2}…).", strNome, lngTamanho / (1024.0 * 1024.0), strHashCopia.Substring(0, 16)));
                    lstCopiados.Add(new TCdExtraFile { strNomeArquivo = strNome, strCaminhoCompleto = strDestino, lngTamanhoBytes = lngTamanho, strSha256 = strHashCopia });
                }
                catch (Exception ex)
                {
                    actLog(string.Format("⚠ CD Extra: não consegui copiar '{0}': {1}", strNome, ex.Message));
                    try { if (File.Exists(strDestino)) File.Delete(strDestino); } catch { }
                }
            }
            return lstCopiados;
        }

        private static string HashDe(string strArquivo)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = new FileStream(strArquivo, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                return Hex(sha.ComputeHash(fs));
        }

        private static string Hex(byte[] arr)
        {
            return BitConverter.ToString(arr).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Minera de forma autônoma e resiliente a capa oficial de fábrica e os metadados
        /// ricos contidos nos arquivos da sessão de dados de um CD Extra / Enhanced CD / Copy Control.
        /// Avalia imagens por resolução e aspect ratio quadrado (~1:1) e lê múltiplos formatos de
        /// metadados (ASX, WAX, CDS300 trackinfo.xml).
        /// </summary>
        public static bool MinerarSessaoDados(string strPastaOrigem, Action<string> actLog, out TMetadadosCdExtra objMetadados)
        {
            objMetadados = new TMetadadosCdExtra();
            if (actLog == null) actLog = delegate { };

            if (string.IsNullOrEmpty(strPastaOrigem) || !Directory.Exists(strPastaOrigem))
            {
                return false;
            }

            try
            {
                actLog(string.Format("TCdExtraExtractor: Iniciando mineração de artes e metadados na pasta '{0}'...", strPastaOrigem));

                // 1. Mineração da Capa de Alta Resolução (~1:1 com pontuação por heurística visual)
                string[] arrTodosArquivos = Directory.GetFiles(strPastaOrigem, "*.*", SearchOption.AllDirectories);

                string strMelhorImagem = null;
                long lngMaiorScore = long.MinValue;
                int intMelhorLargura = 0;
                int intMelhorAltura = 0;

                foreach (string strArquivo in arrTodosArquivos)
                {
                    string strExt = Path.GetExtension(strArquivo).ToLowerInvariant();
                    if (strExt != ".jpg" && strExt != ".jpeg" && strExt != ".png" && strExt != ".bmp")
                    {
                        continue;
                    }

                    try
                    {
                        FileInfo fi = new FileInfo(strArquivo);
                        if (fi.Length < 1024) continue; // Ignora ícones ou placeholders minúsculos

                        int intLargura = 0;
                        int intAltura = 0;

                        using (FileStream fsImg = new FileStream(strArquivo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            using (Image img = Image.FromStream(fsImg, false, false))
                            {
                                intLargura = img.Width;
                                intAltura = img.Height;
                            }
                        }

                        if (intLargura < 200 || intAltura < 200) continue;

                        double dblRatio = (double)intLargura / intAltura;
                        // Encartes de CD têm formato estritamente quadrado (tolerância entre 0.85 e 1.18)
                        if (dblRatio < 0.85 || dblRatio > 1.18) continue;

                        // Cálculo do Score Heurístico
                        long lngScore = ((long)intLargura * intAltura) + (fi.Length / 2);
                        string strNomeLower = Path.GetFileNameWithoutExtension(strArquivo).ToLowerInvariant();

                        // Bônus para nomes consagrados de capa oficial
                        if (strNomeLower.Contains("cover") || strNomeLower.Contains("front") ||
                            strNomeLower.Contains("background") || strNomeLower.Contains("album") ||
                            strNomeLower.Contains("folder") || strNomeLower.Contains("capa") ||
                            strNomeLower.Contains("artwork"))
                        {
                            lngScore += 250000;
                        }

                        // Penalidade para skins, botões ou elementos de interface do player proprietário
                        if (strNomeLower.Contains("gui") || strNomeLower.Contains("skin") ||
                            strNomeLower.Contains("btn") || strNomeLower.Contains("button") ||
                            strNomeLower.Contains("logo") || strNomeLower.Contains("banner") ||
                            strNomeLower.Contains("icon") || strNomeLower.Contains("thumb") ||
                            strNomeLower.Contains("slider"))
                        {
                            lngScore -= 1000000;
                        }

                        if (lngScore > lngMaiorScore)
                        {
                            lngMaiorScore = lngScore;
                            strMelhorImagem = strArquivo;
                            intMelhorLargura = intLargura;
                            intMelhorAltura = intAltura;
                        }
                    }
                    catch
                    {
                        // Arquivo de imagem ilegível ou corrompido, ignora silenciosamente
                    }
                }

                if (!string.IsNullOrEmpty(strMelhorImagem) && lngMaiorScore > 0)
                {
                    try
                    {
                        objMetadados.arrCapaBytes = File.ReadAllBytes(strMelhorImagem);
                        objMetadados.strCaminhoCapaOriginal = strMelhorImagem;
                        objMetadados.intLarguraCapa = intMelhorLargura;
                        objMetadados.intAlturaCapa = intMelhorAltura;

                        actLog(string.Format("TCdExtraExtractor: Capa oficial localizada: '{0}' ({1}x{2}, {3:F1} KB).",
                            Path.GetFileName(strMelhorImagem), intMelhorLargura, intMelhorAltura, objMetadados.arrCapaBytes.Length / 1024.0));
                    }
                    catch (Exception exCapa)
                    {
                        actLog("TCdExtraExtractor: Erro ao carregar bytes da capa: " + exCapa.Message);
                    }
                }

                // 2. Mineração de Metadados Ricos (XML, WAX, ASX)
                List<string> lstArquivosMeta = new List<string>();
                foreach (string strArquivo in arrTodosArquivos)
                {
                    string strExt = Path.GetExtension(strArquivo).ToLowerInvariant();
                    if (strExt == ".xml" || strExt == ".wax" || strExt == ".asx" || strExt == ".inf" || strExt == ".ini")
                    {
                        lstArquivosMeta.Add(strArquivo);
                    }
                }

                // Prioriza na ordem: *.wax / *.asx (geralmente contém compositores), depois *.xml
                lstArquivosMeta.Sort((a, b) =>
                {
                    string extA = Path.GetExtension(a).ToLowerInvariant();
                    string extB = Path.GetExtension(b).ToLowerInvariant();
                    int pA = (extA == ".wax" || extA == ".asx") ? 0 : (extA == ".xml" ? 1 : 2);
                    int pB = (extB == ".wax" || extB == ".asx") ? 0 : (extB == ".xml" ? 1 : 2);
                    return pA.CompareTo(pB);
                });

                foreach (string strArquivoMeta in lstArquivosMeta)
                {
                    try
                    {
                        string strConteudo = LerTextoDetectando(strArquivoMeta);
                        if (string.IsNullOrWhiteSpace(strConteudo)) continue;

                        // Se o arquivo contiver estrutura ASX/WAX
                        if (strConteudo.IndexOf("<ENTRY", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            strConteudo.IndexOf("<ASX", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            ParsearAsxWax(strConteudo, objMetadados, actLog);
                        }
                        // Se o arquivo contiver estrutura CDS300 / TrackInfo
                        else if (strConteudo.IndexOf("<cds300", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 strConteudo.IndexOf("<track id=", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            ParsearCds300(strConteudo, objMetadados, actLog);
                        }
                    }
                    catch
                    {
                        // Continua tentando outros arquivos de metadados
                    }
                }

                bool blnTemAlgo = (objMetadados.arrCapaBytes != null && objMetadados.arrCapaBytes.Length > 0) ||
                                  (objMetadados.lstTitulosFaixas.Count > 0) ||
                                  (!string.IsNullOrEmpty(objMetadados.strAlbum));

                if (blnTemAlgo)
                {
                    actLog(string.Format("TCdExtraExtractor: Mineração concluída com sucesso! Álbum: '{0}', Artista: '{1}', {2} faixas, {3} compositores.",
                        objMetadados.strAlbum ?? "N/D",
                        objMetadados.strArtistaAlbum ?? "N/D",
                        objMetadados.lstTitulosFaixas.Count,
                        objMetadados.lstCompositoresFaixas.Count));
                }

                return blnTemAlgo;
            }
            catch (Exception ex)
            {
                actLog("TCdExtraExtractor: Falha durante mineração da sessão de dados: " + ex.Message);
                return false;
            }
        }

        private static void ParsearAsxWax(string strConteudo, TMetadadosCdExtra objMeta, Action<string> actLog)
        {
            try
            {
                Match mTituloAlbum = Regex.Match(strConteudo, @"<TITLE>\s*([^<]+?)\s*</TITLE>", RegexOptions.IgnoreCase);
                if (mTituloAlbum.Success && string.IsNullOrEmpty(objMeta.strAlbum))
                {
                    objMeta.strAlbum = LimparTextoXml(mTituloAlbum.Groups[1].Value);
                }

                Match mAutor = Regex.Match(strConteudo, @"<AUTHOR>\s*([^<]+?)\s*</AUTHOR>", RegexOptions.IgnoreCase);
                if (mAutor.Success && string.IsNullOrEmpty(objMeta.strArtistaAlbum))
                {
                    objMeta.strArtistaAlbum = LimparTextoXml(mAutor.Groups[1].Value);
                }
                else
                {
                    Match mArtistaParam = Regex.Match(strConteudo, @"<PARAM\s+NAME\s*=\s*""ARTIST""\s+VALUE\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
                    if (mArtistaParam.Success && string.IsNullOrEmpty(objMeta.strArtistaAlbum))
                    {
                        objMeta.strArtistaAlbum = LimparTextoXml(mArtistaParam.Groups[1].Value);
                    }
                }

                Match mAno = Regex.Match(strConteudo, @"<PARAM\s+NAME\s*=\s*""YEAR""\s+VALUE\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
                if (mAno.Success && string.IsNullOrEmpty(objMeta.strAno))
                {
                    objMeta.strAno = LimparTextoXml(mAno.Groups[1].Value);
                }

                MatchCollection colEntries = Regex.Matches(strConteudo, @"<ENTRY\b[^>]*>(.*?)</ENTRY>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (colEntries.Count > 0)
                {
                    int intIndice = 0;
                    foreach (Match mEntry in colEntries)
                    {
                        string strBloco = mEntry.Groups[1].Value;

                        Match mTrkTitle = Regex.Match(strBloco, @"<TITLE>\s*([^<]+?)\s*</TITLE>", RegexOptions.IgnoreCase);
                        string strTrkTitle = mTrkTitle.Success ? LimparTextoXml(mTrkTitle.Groups[1].Value) : null;

                        Match mTrkArtist = Regex.Match(strBloco, @"<PARAM\s+NAME\s*=\s*""ARTIST""\s+VALUE\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
                        string strTrkArtist = mTrkArtist.Success ? LimparTextoXml(mTrkArtist.Groups[1].Value) : null;
                        if (string.IsNullOrEmpty(strTrkArtist))
                        {
                            Match mAuthor = Regex.Match(strBloco, @"<AUTHOR>\s*([^<]+?)\s*</AUTHOR>", RegexOptions.IgnoreCase);
                            if (mAuthor.Success) strTrkArtist = LimparTextoXml(mAuthor.Groups[1].Value);
                        }

                        Match mComposer = Regex.Match(strBloco, @"<PARAM\s+NAME\s*=\s*""COMPOSER""\s+VALUE\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
                        string strComposer = mComposer.Success ? LimparTextoXml(mComposer.Groups[1].Value) : null;

                        DefinirNaPosicao(objMeta.lstTitulosFaixas, intIndice, strTrkTitle);

                        DefinirNaPosicao(objMeta.lstArtistasFaixas, intIndice, strTrkArtist);

                        if (!string.IsNullOrEmpty(strComposer))
                        {
                            while (objMeta.lstCompositoresFaixas.Count < intIndice)
                            {
                                objMeta.lstCompositoresFaixas.Add("");
                            }
                            if (intIndice < objMeta.lstCompositoresFaixas.Count)
                            {
                                objMeta.lstCompositoresFaixas[intIndice] = strComposer;
                            }
                            else
                            {
                                objMeta.lstCompositoresFaixas.Add(strComposer);
                            }
                        }

                        intIndice++;
                    }
                }
            }
            catch (Exception ex)
            {
                actLog("TCdExtraExtractor: Aviso no parser ASX: " + ex.Message);
            }
        }

        private static void ParsearCds300(string strConteudo, TMetadadosCdExtra objMeta, Action<string> actLog)
        {
            try
            {
                Match mAlbumTitle = Regex.Match(strConteudo, @"<album>.*?<title>\s*([^<]+?)\s*</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (mAlbumTitle.Success && string.IsNullOrEmpty(objMeta.strAlbum))
                {
                    objMeta.strAlbum = LimparTextoXml(mAlbumTitle.Groups[1].Value);
                }

                Match mAlbumArtist = Regex.Match(strConteudo, @"<album>.*?<artist>\s*([^<]+?)\s*</artist>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (mAlbumArtist.Success && string.IsNullOrEmpty(objMeta.strArtistaAlbum))
                {
                    objMeta.strArtistaAlbum = LimparTextoXml(mAlbumArtist.Groups[1].Value);
                }

                Match mAlbumYear = Regex.Match(strConteudo, @"<album>.*?<year>\s*([^<]+?)\s*</year>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (mAlbumYear.Success && string.IsNullOrEmpty(objMeta.strAno))
                {
                    objMeta.strAno = LimparTextoXml(mAlbumYear.Groups[1].Value);
                }

                MatchCollection colTracks = Regex.Matches(strConteudo, @"<track\b[^>]*>(.*?)</track>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (colTracks.Count > 0)
                {
                    int intIndice = 0;
                    foreach (Match mTrack in colTracks)
                    {
                        string strBloco = mTrack.Groups[1].Value;

                        Match mTrkTitle = Regex.Match(strBloco, @"<title>\s*([^<]+?)\s*</title>", RegexOptions.IgnoreCase);
                        string strTrkTitle = mTrkTitle.Success ? LimparTextoXml(mTrkTitle.Groups[1].Value) : null;

                        Match mTrkArtist = Regex.Match(strBloco, @"<artist>\s*([^<]+?)\s*</artist>", RegexOptions.IgnoreCase);
                        string strTrkArtist = mTrkArtist.Success ? LimparTextoXml(mTrkArtist.Groups[1].Value) : null;

                        DefinirNaPosicao(objMeta.lstTitulosFaixas, intIndice, strTrkTitle);

                        DefinirNaPosicao(objMeta.lstArtistasFaixas, intIndice, strTrkArtist);

                        intIndice++;
                    }
                }
            }
            catch (Exception ex)
            {
                actLog("TCdExtraExtractor: Aviso no parser CDS300: " + ex.Message);
            }
        }

        /// <summary>
        /// Entidades XML/HTML decodificadas numa passada só: as numéricas
        /// (&amp;#233; → é) também, e "&amp;amp;lt;" vira "&amp;lt;", não "&lt;".
        /// </summary>
        private static string LimparTextoXml(string strTexto)
        {
            if (string.IsNullOrEmpty(strTexto)) return "";
            return System.Net.WebUtility.HtmlDecode(strTexto.Trim());
        }

        /// <summary>
        /// Põe o valor na posição da entrada (faixa), preenchendo com vazio o
        /// que faltar antes. Antes, uma entrada sem título não ocupava lugar, e
        /// os títulos seguintes caíam uma faixa adiantados. Não sobrescreve o
        /// que outra fonte já preencheu.
        /// </summary>
        private static void DefinirNaPosicao(List<string> lst, int intIndice, string strValor)
        {
            if (string.IsNullOrEmpty(strValor)) return;
            while (lst.Count <= intIndice) lst.Add("");
            if (string.IsNullOrEmpty(lst[intIndice])) lst[intIndice] = strValor;
        }

        /// <summary>
        /// Lê um arquivo de texto do disco respeitando a codificação: BOM se
        /// houver; senão UTF-8 se o conteúdo for UTF-8 válido; senão a
        /// codificação do Windows (1252). Ler tudo como UTF-8 destruía os
        /// acentos dos ASX/WAX gravados em ANSI.
        /// </summary>
        private static string LerTextoDetectando(string strArquivo)
        {
            byte[] arr = File.ReadAllBytes(strArquivo);
            if (arr.Length >= 2 && arr[0] == 0xFF && arr[1] == 0xFE) return Encoding.Unicode.GetString(arr, 2, arr.Length - 2);
            if (arr.Length >= 2 && arr[0] == 0xFE && arr[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(arr, 2, arr.Length - 2);
            if (arr.Length >= 3 && arr[0] == 0xEF && arr[1] == 0xBB && arr[2] == 0xBF) return Encoding.UTF8.GetString(arr, 3, arr.Length - 3);
            try { return new UTF8Encoding(false, true).GetString(arr); }
            catch (DecoderFallbackException) { return Encoding.GetEncoding(1252).GetString(arr); }
        }

    }
}
