using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ARCA.Modelo;

namespace ARCA.Identificar
{
    /// <summary>O que vale para o álbum inteiro, já conferido pela pessoa na janela.</summary>
    public sealed class TDadosAlbum
    {
        public string strArtistaAlbum;
        public string strAlbum;
        public string strAno;
        public string strGenero;
        public int intTotalFaixas;
        public string strFonte;          // "Discogs · Som Livre 2339-2 · Brazil · release 26816816"
        public byte[] arrCapa;           // null = não mexer na capa
        /// <summary>Capa sem redução, só quando veio do disco físico (scanner ou arquivo): vai para Artwork\capa.jpg.</summary>
        public byte[] arrCapaPlena;
        /// <summary>Páginas do encarte digitalizadas (JPEG pleno), na ordem: Artwork\encarte_NN.jpg.</summary>
        public List<byte[]> lstEncarte;
        /// <summary>De onde veio a capa, para o ledger: "Discogs", "digitalizada (G7000)", "arquivo"…</summary>
        public string strOrigemCapa;
        /// <summary>O evento no ledger; null = identificação de um álbum.</summary>
        public string strEvento;
        /// <summary>Ao mover, dar à faixa o nome "NN Título.mp3" (os do Unknown Album vêm truncados: "Mozart, Wolfgang Amadeus-The Magic F 12").</summary>
        public bool blnRenomearAoMover;
    }

    /// <summary>
    /// Grava a identificação nos MP3 e WMA sem nunca arriscar o áudio:
    ///
    ///  1. copia a faixa para "nome.mp3.arca-partial", na mesma pasta;
    ///  2. grava as tags e a capa na cópia: MP3 em ID3v2.3 (taglib-sharp);
    ///     WMA pelo Windows Media Format SDK, o motor do próprio WMP (Comum\EditorAsf);
    ///  3. confere: o SHA-256 dos bytes de áudio da cópia tem de ser o do original;
    ///  4. só então troca (File.Replace), e confere de novo no arquivo final.
    ///
    /// Se qualquer passo falha, o original fica como estava. Depois das faixas,
    /// Folder.jpg (200 px) e AlbumArtSmall.jpg (75 px), ocultos e de sistema,
    /// como o WMP faz. Tudo vai para o ledger do acervo (append-only).
    /// </summary>
    public static class GravadorMp3
    {
        public const int CAPA_EMBUTIDA_MAX = 1000;

        public static int Aplicar(List<TFaixaMp3> lstFaixas, TDadosAlbum objAlbum, Action<string> actLog, Action<int, int> actProgresso)
        {
            return Aplicar(lstFaixas, objAlbum, actLog, actProgresso, null, true);
        }

        /// <param name="strPastaDestino">Se não for null, cada faixa gravada é movida para lá (mesmo disco; nome que já existe não é sobrescrito).</param>
        /// <param name="blnImagensDePasta">false quando a pasta tem faixas de outros discos: Folder.jpg seria de um só.</param>
        public static int Aplicar(List<TFaixaMp3> lstFaixas, TDadosAlbum objAlbum, Action<string> actLog, Action<int, int> actProgresso,
                                  string strPastaDestino, bool blnImagensDePasta)
        {
            int intTotal = lstFaixas.Count(x => x.blnAplicar), intFeitas = 0;
            TagLib.Id3v2.Tag.DefaultVersion = 3;
            TagLib.Id3v2.Tag.ForceDefaultVersion = true;

            byte[] arrCapaEmbutida = null;
            if (objAlbum.arrCapa != null)
            {
                arrCapaEmbutida = Reduzir(objAlbum.arrCapa, CAPA_EMBUTIDA_MAX, 90);
                actLog(string.Format("Capa: {0:N0} bytes da fonte; embutida com {1:N0} bytes (até {2} px).",
                    objAlbum.arrCapa.Length, arrCapaEmbutida.Length, CAPA_EMBUTIDA_MAX));
            }

            List<Dictionary<string, object>> lstLedger = new List<Dictionary<string, object>>();
            int intOk = 0;
            foreach (TFaixaMp3 f in lstFaixas.Where(x => x.blnAplicar))
            {
                actProgresso(++intFeitas, intTotal);
                Dictionary<string, object> dicAntes = Resumo(f.strTitulo, f.strArtista, f.strArtistaAlbum, f.strAlbum, f.uintAno.ToString(), f.intFaixaTag);
                try
                {
                    string strHash = GravarFaixa(f, objAlbum, arrCapaEmbutida);
                    intOk++;
                    f.strResultado = "gravada · áudio intacto";
                    actLog("OK  " + f.strArquivo);
                    Dictionary<string, object> dic = new Dictionary<string, object>();
                    dic["arquivo"] = f.strArquivo;
                    dic["sha256Audio"] = strHash;
                    dic["antes"] = dicAntes;
                    dic["depois"] = Resumo(f.strTituloNovo, f.strArtistaNovo, objAlbum.strArtistaAlbum, objAlbum.strAlbum, objAlbum.strAno, f.intPosicaoNova + 1);
                    lstLedger.Add(dic);
                    if (strPastaDestino != null && !string.Equals(Path.GetFullPath(Path.GetDirectoryName(f.strCaminho)).TrimEnd('\\'),
                                                                  Path.GetFullPath(strPastaDestino).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    {
                        string strNovo = Path.Combine(strPastaDestino, objAlbum.blnRenomearAoMover ? NomeDaFaixa(f) : f.strArquivo);
                        if (File.Exists(strNovo)) actLog("   não movida: já existe " + strNovo);
                        else
                        {
                            Directory.CreateDirectory(strPastaDestino);
                            File.Move(f.strCaminho, strNovo);
                            dic["movidoDe"] = f.strCaminho;
                            dic["arquivo"] = strNovo;
                            actLog("   movida para " + strPastaDestino);
                            f.strCaminho = strNovo;
                            f.strResultado = "gravada · áudio intacto · movida";
                        }
                    }
                }
                catch (Exception ex)
                {
                    f.strResultado = "NÃO gravada: " + ex.Message;
                    actLog("FALHA  " + f.strArquivo + ": " + ex.Message + "  (o arquivo original não foi alterado)");
                }
            }

            string strPasta = strPastaDestino != null && Directory.Exists(strPastaDestino) ? strPastaDestino : Path.GetDirectoryName(lstFaixas[0].strCaminho);
            if (!blnImagensDePasta && objAlbum.arrCapa != null && intOk > 0)
                actLog("Folder.jpg não gravado: a pasta tem faixas de outros discos (a capa está embutida em cada faixa).");
            if (blnImagensDePasta && objAlbum.arrCapa != null && intOk > 0)
            {
                try
                {
                    GravarImagemDePasta(Path.Combine(strPasta, "Folder.jpg"), Reduzir(objAlbum.arrCapa, 200, 90));
                    GravarImagemDePasta(Path.Combine(strPasta, "AlbumArtSmall.jpg"), Reduzir(objAlbum.arrCapa, 75, 90));
                    actLog("Folder.jpg e AlbumArtSmall.jpg gravados (ocultos, de sistema).");
                    // Arte que o próprio WMP baixou antes (AlbumArt_{GUID}_*.jpg) passaria
                    // na frente da nova. Pode ser a única cópia dessa arte (o serviço de
                    // metadados do WMP não existe mais): não se apaga, vira backup datado,
                    // oculto como o original, fora da vista do WMP.
                    foreach (string strVelha in Directory.GetFiles(strPasta, "AlbumArt_{*}_*.jpg"))
                    {
                        string strBackup = NomeDeBackup(strVelha);
                        File.SetAttributes(strVelha, FileAttributes.Normal);
                        File.Move(strVelha, strBackup);
                        File.SetAttributes(strBackup, FileAttributes.Hidden | FileAttributes.System);
                        actLog("Arte antiga do WMP guardada (oculta) como " + Path.GetFileName(strBackup));
                    }
                }
                catch (Exception ex) { actLog("ATENÇÃO: capa da pasta não gravada: " + ex.Message); }
            }

            List<string> lstArtwork = new List<string>();
            if (intOk > 0)
            {
                try { lstArtwork = GravarArtwork(strPasta, objAlbum, actLog); }
                catch (Exception ex) { actLog("ATENÇÃO: Artwork\\ não gravado: " + ex.Message); }
            }

            if (lstLedger.Count > 0)
            {
                try { EscreverLedger(strPasta, objAlbum, lstLedger, lstArtwork); actLog("Ledger: registrado em " + CaminhoDoLedger()); }
                catch (Exception ex) { actLog("ATENÇÃO: não consegui gravar o ledger (" + ex.Message + "). As faixas foram gravadas."); }
            }
            return intOk;
        }

        /// <summary>
        /// Depois de mover as faixas: se a pasta de origem ficou só com imagens
        /// derivadas (Folder.jpg, AlbumArtSmall.jpg, Thumbs.db, desktop.ini) e
        /// arte baixada pelo WMP (AlbumArt_*), a arte vai para o destino como
        /// backup oculto, o resto sai, e a pasta vazia também. Qualquer outro
        /// arquivo (música, encarte, texto) faz a pasta ficar como está.
        /// </summary>
        public static void LimparPastaEsvaziada(string strPasta, string strDestino, Action<string> actLog)
        {
            if (!Directory.Exists(strPasta) || Directory.GetDirectories(strPasta).Length > 0) return;
            string[] vet = Directory.GetFiles(strPasta);
            Func<string, bool> blnDerivado = s =>
            {
                string n = Path.GetFileName(s).ToLowerInvariant();
                return n == "folder.jpg" || n == "albumartsmall.jpg" || n == "thumbs.db" || n == "desktop.ini" ||
                       (n.StartsWith("albumart_") && (n.EndsWith(".jpg") || n.Contains(".jpg.backup-")));
            };
            if (vet.Any(s => !blnDerivado(s))) return;
            foreach (string s in vet)
            {
                File.SetAttributes(s, FileAttributes.Normal);
                if (Path.GetFileName(s).StartsWith("AlbumArt_", StringComparison.OrdinalIgnoreCase))
                {
                    string strNovo = Path.Combine(strDestino, Path.GetFileName(s));
                    if (!strNovo.Contains(".backup-")) strNovo = NomeDeBackup(strNovo);
                    if (File.Exists(strNovo)) File.Delete(s);
                    else { File.Move(s, strNovo); File.SetAttributes(strNovo, FileAttributes.Hidden | FileAttributes.System); }
                }
                else File.Delete(s);
            }
            Directory.Delete(strPasta);
            actLog("Pasta antiga esvaziada e removida: " + strPasta + " (a arte que o WMP tinha baixado foi junto, oculta)");
        }

        /// <summary>
        /// O caminho seguro de toda gravação de tag: cópia "x.arca-partial", a
        /// edição nela, o áudio conferido (SHA-256 sem as tags), a troca com
        /// backup e a conferência de novo. O original só muda se o áudio da cópia
        /// for idêntico. Devolve o hash do áudio.
        /// </summary>
        public static string EditarComCopiaConferida(string strOriginal, bool blnWma, Action<string> actEditar)
        {
            string strParcial = strOriginal + ".arca-partial";
            string strBackup = strOriginal + ".arca-backup";
            string strHashOriginal = TFaixaMp3.HashDoAudio(strOriginal, blnWma);

            if (File.Exists(strParcial)) File.Delete(strParcial);
            File.Copy(strOriginal, strParcial);
            File.SetAttributes(strParcial, FileAttributes.Normal);
            try
            {
                actEditar(strParcial);
                if (TFaixaMp3.HashDoAudio(strParcial, blnWma) != strHashOriginal)
                    throw new IOException("a cópia gravada mudou o áudio — descartada");
                if (File.Exists(strBackup)) File.Delete(strBackup);
                File.Replace(strParcial, strOriginal, strBackup);
            }
            finally
            {
                if (File.Exists(strParcial)) File.Delete(strParcial);
            }
            if (TFaixaMp3.HashDoAudio(strOriginal, blnWma) != strHashOriginal)
            {
                File.Replace(strBackup, strOriginal, null);
                throw new IOException("o arquivo final não conferiu; o original foi restaurado");
            }
            File.Delete(strBackup);
            return strHashOriginal;
        }

        // ------------------------------------------------------------ classe de mídia (música / audiolivro / fala)

        private const string PRIV_PRIMARIA = "WM/MediaClassPrimaryID", PRIV_SECUNDARIA = "WM/MediaClassSecondaryID";

        /// <summary>
        /// A classe de mídia da faixa: no WMA, os atributos WM/MediaClass*; no
        /// MP3, os mesmos como quadros ID3 PRIV (é onde o WMP os procura). Sem
        /// marca, ou com marca que não é nossa, é música.
        /// </summary>
        public static ConteudoFaixa LerClasse(string strArquivo)
        {
            try
            {
                if (TFaixaMp3.EhWma(strArquivo))
                    using (ARCA.Interop.EditorAsf e = new ARCA.Interop.EditorAsf())
                        return e.Abrir(strArquivo) ? TClasseMidia.Ler(e.LerTudo()) : ConteudoFaixa.Musica;
                using (TagLib.File t = TagLib.File.Create(strArquivo, "taglib/mp3", TagLib.ReadStyle.None))
                {
                    TagLib.Id3v2.Tag id3 = t.GetTag(TagLib.TagTypes.Id3v2) as TagLib.Id3v2.Tag;
                    if (id3 == null) return ConteudoFaixa.Musica;
                    TagLib.Id3v2.PrivateFrame p1 = TagLib.Id3v2.PrivateFrame.Get(id3, PRIV_PRIMARIA, false);
                    TagLib.Id3v2.PrivateFrame p2 = TagLib.Id3v2.PrivateFrame.Get(id3, PRIV_SECUNDARIA, false);
                    Guid g1 = p1 != null && p1.PrivateData != null && p1.PrivateData.Count == 16 ? new Guid(p1.PrivateData.Data) : Guid.Empty;
                    Guid g2 = p2 != null && p2.PrivateData != null && p2.PrivateData.Count == 16 ? new Guid(p2.PrivateData.Data) : Guid.Empty;
                    if (g1 != TClasseMidia.guidPrimariaAudioNaoMusica) return ConteudoFaixa.Musica;
                    return g2 == TClasseMidia.guidSecundariaAudiolivro ? ConteudoFaixa.Audiolivro : ConteudoFaixa.Fala;
                }
            }
            catch { return ConteudoFaixa.Musica; }
        }

        /// <summary>Grava a classe de mídia (cópia conferida, como toda gravação) e registra no ledger. Lança em falha.</summary>
        public static void GravarClasse(string strArquivo, ConteudoFaixa eConteudo, ConteudoFaixa eAntes)
        {
            bool blnWma = TFaixaMp3.EhWma(strArquivo);
            string strHash = EditarComCopiaConferida(strArquivo, blnWma, delegate(string strParcial)
            {
                if (blnWma)
                    using (ARCA.Interop.EditorAsf e = new ARCA.Interop.EditorAsf())
                    {
                        if (!e.Abrir(strParcial)) throw new IOException("o WMA não abriu para edição: " + e.Falha);
                        Exigir(e, TClasseMidia.Gravar(e, eConteudo));
                        Exigir(e, e.Salvar());
                    }
                else
                    using (TagLib.File t = TagLib.File.Create(strParcial, "taglib/mp3", TagLib.ReadStyle.Average))
                    {
                        TagLib.Id3v2.Tag id3 = (TagLib.Id3v2.Tag)t.GetTag(TagLib.TagTypes.Id3v2, true);
                        Guid g1 = eConteudo == ConteudoFaixa.Musica ? TClasseMidia.guidPrimariaMusica : TClasseMidia.guidPrimariaAudioNaoMusica;
                        TagLib.Id3v2.PrivateFrame.Get(id3, PRIV_PRIMARIA, true).PrivateData = new TagLib.ByteVector(g1.ToByteArray());
                        TagLib.Id3v2.PrivateFrame p2 = TagLib.Id3v2.PrivateFrame.Get(id3, PRIV_SECUNDARIA, eConteudo != ConteudoFaixa.Musica);
                        if (eConteudo == ConteudoFaixa.Musica) { if (p2 != null) id3.RemoveFrame(p2); }
                        else p2.PrivateData = new TagLib.ByteVector((eConteudo == ConteudoFaixa.Audiolivro ? TClasseMidia.guidSecundariaAudiolivro : TClasseMidia.guidSecundariaFala).ToByteArray());
                        t.Save();
                    }
            });
            Dictionary<string, object> obj = new Dictionary<string, object>();
            obj["quando"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            obj["evento"] = "classe de mídia (ARCA Identificar)";
            obj["arquivo"] = strArquivo;
            obj["sha256Audio"] = strHash;
            obj["antes"] = eAntes.ToString();
            obj["depois"] = eConteudo.ToString();
            try { File.AppendAllText(CaminhoDoLedger(), new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(obj) + "\n", new UTF8Encoding(false)); }
            catch { }
        }

        private static string GravarFaixa(TFaixaMp3 f, TDadosAlbum a, byte[] arrCapa)
        {
            return EditarComCopiaConferida(f.strCaminho, f.blnWma, delegate(string strParcial)
            {
                if (f.blnWma) GravarTagsWma(strParcial, f, a, arrCapa);
                else
                using (TagLib.File t = TagLib.File.Create(strParcial, "taglib/mp3", TagLib.ReadStyle.Average))
                {
                    // Quadros da tag quebrada do iTunes antigo ("TT2\0", "TP1 "…): saem; os certos são escritos abaixo.
                    TagLib.Id3v2.Tag objId3 = t.GetTag(TagLib.TagTypes.Id3v2) as TagLib.Id3v2.Tag;
                    if (objId3 != null)
                        foreach (TagLib.Id3v2.Frame q in objId3.GetFrames().ToList())
                        {
                            TagLib.ByteVector id = q.FrameId;
                            if (id.Count == 4 && (id[3] == 0 || id[3] == 0x20)) objId3.RemoveFrame(q);
                        }
                    t.Tag.Title = f.strTituloNovo;
                    string strInterprete = string.IsNullOrEmpty(f.strArtistaNovo) ? a.strArtistaAlbum : f.strArtistaNovo;
                    t.Tag.Performers = new[] { strInterprete };
                    t.Tag.AlbumArtists = new[] { a.strArtistaAlbum };
                    t.Tag.Album = a.strAlbum;
                    uint uintAno;
                    t.Tag.Year = uint.TryParse((a.strAno ?? "").Trim(), out uintAno) ? uintAno : 0;
                    if (!string.IsNullOrEmpty(a.strGenero)) t.Tag.Genres = new[] { a.strGenero };
                    t.Tag.Track = (uint)(f.intPosicaoNova + 1);
                    t.Tag.TrackCount = (uint)a.intTotalFaixas;
                    if (!string.IsNullOrEmpty(f.strCompositorNovo)) t.Tag.Composers = new[] { f.strCompositorNovo };
                    if (arrCapa != null)
                    {
                        TagLib.Picture objCapa = new TagLib.Picture(new TagLib.ByteVector(arrCapa));
                        objCapa.Type = TagLib.PictureType.FrontCover;
                        objCapa.MimeType = "image/jpeg";
                        objCapa.Description = "";
                        t.Tag.Pictures = new TagLib.IPicture[] { objCapa };
                    }
                    t.Save();
                }
            });
        }

        /// <summary>
        /// WMA: os atributos gravados ao catalogar um rip
        /// (ProcessadorExtracaoAudio), mais WM/TrackNumber em DWORD, como o WMP.
        /// </summary>
        private static void GravarTagsWma(string strParcial, TFaixaMp3 f, TDadosAlbum a, byte[] arrCapa)
        {
            string strInterprete = string.IsNullOrEmpty(f.strArtistaNovo) ? a.strArtistaAlbum : f.strArtistaNovo;
            using (ARCA.Interop.EditorAsf e = new ARCA.Interop.EditorAsf())
            {
                if (!e.Abrir(strParcial)) throw new IOException("o WMA não abriu para edição: " + e.Falha);
                Exigir(e, e.DefinirTexto("Title", f.strTituloNovo ?? ""));
                Exigir(e, e.DefinirTexto("Author", strInterprete ?? ""));
                Exigir(e, e.DefinirTexto("WM/AlbumArtist", a.strArtistaAlbum ?? ""));
                Exigir(e, e.DefinirTexto("WM/AlbumTitle", a.strAlbum ?? ""));
                if (!string.IsNullOrEmpty(a.strAno)) Exigir(e, e.DefinirTexto("WM/Year", a.strAno.Trim()));
                if (!string.IsNullOrEmpty(a.strGenero)) Exigir(e, e.DefinirTexto("WM/Genre", a.strGenero.Trim()));
                if (!string.IsNullOrEmpty(f.strCompositorNovo)) Exigir(e, e.DefinirTexto("WM/Composer", f.strCompositorNovo.Trim()));
                if (arrCapa != null) Exigir(e, e.DefinirImagem(arrCapa, null, "Cover"));
                Exigir(e, e.Salvar());
            }
            NumeroAsf.DefinirNumeroDaFaixa(strParcial, (uint)(f.intPosicaoNova + 1));
        }

        private static void Exigir(ARCA.Interop.EditorAsf e, bool blnOk)
        {
            if (!blnOk) throw new IOException(e.Falha ?? "o editor ASF recusou a gravação");
        }

        /// <summary>
        /// Capa plena e páginas do encarte que vieram do disco físico, em
        /// Artwork\ — a convenção do ARCA (capa.jpg, encarte_NN.jpg). Capa da
        /// internet não vai para lá: ela está embutida nas faixas. Nada é
        /// sobrescrito: capa.jpg antiga vira backup; páginas novas continuam a
        /// numeração. Devolve os caminhos relativos gravados.
        /// </summary>
        private static List<string> GravarArtwork(string strPasta, TDadosAlbum a, Action<string> actLog)
        {
            List<string> lst = new List<string>();
            bool blnEncarte = a.lstEncarte != null && a.lstEncarte.Count > 0;
            if (a.arrCapaPlena == null && !blnEncarte) return lst;
            string strArt = Path.Combine(strPasta, "Artwork");
            Directory.CreateDirectory(strArt);
            if (a.arrCapaPlena != null)
            {
                string strCapa = Path.Combine(strArt, "capa.jpg");
                if (File.Exists(strCapa))
                {
                    string strBackup = NomeDeBackup(strCapa);
                    File.Move(strCapa, strBackup);
                    actLog("Artwork\\capa.jpg anterior guardada como " + Path.GetFileName(strBackup));
                }
                File.WriteAllBytes(strCapa, a.arrCapaPlena);
                lst.Add("Artwork\\capa.jpg");
            }
            if (blnEncarte)
            {
                int intMaior = 0;
                foreach (string s in Directory.GetFiles(strArt, "encarte_*.jpg"))
                {
                    int n;
                    string strNum = Path.GetFileNameWithoutExtension(s).Substring("encarte_".Length);
                    if (int.TryParse(strNum, out n) && n > intMaior) intMaior = n;
                }
                for (int i = 0; i < a.lstEncarte.Count; i++)
                {
                    string strNome = string.Format("encarte_{0:00}.jpg", intMaior + 1 + i);
                    File.WriteAllBytes(Path.Combine(strArt, strNome), a.lstEncarte[i]);
                    lst.Add("Artwork\\" + strNome);
                }
            }
            actLog("Artwork\\: " + string.Join(", ", lst.ToArray()));
            return lst;
        }

        private static Dictionary<string, object> Resumo(string strTitulo, string strArtista, string strArtistaAlbum, string strAlbum, string strAno, int intFaixa)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["faixa"] = intFaixa;
            d["titulo"] = strTitulo ?? "";
            d["artista"] = strArtista ?? "";
            d["artistaAlbum"] = strArtistaAlbum ?? "";
            d["album"] = strAlbum ?? "";
            d["ano"] = strAno ?? "";
            return d;
        }

        // ------------------------------------------------------------ imagens

        /// <summary>JPEG cabendo em intMax x intMax. Já JPEG e já pequeno: os mesmos bytes.</summary>
        public static byte[] Reduzir(byte[] arrOrigem, int intMax, int intQualidade)
        {
            BitmapImage bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = new MemoryStream(arrOrigem);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            double dblEscala = Math.Min(1.0, (double)intMax / Math.Max(bi.PixelWidth, bi.PixelHeight));
            bool blnJpeg = arrOrigem.Length > 3 && arrOrigem[0] == 0xFF && arrOrigem[1] == 0xD8;
            if (dblEscala >= 1.0 && blnJpeg) return arrOrigem;

            BitmapSource objFonte = bi;
            if (dblEscala < 1.0) objFonte = new TransformedBitmap(bi, new ScaleTransform(dblEscala, dblEscala));
            JpegBitmapEncoder enc = new JpegBitmapEncoder { QualityLevel = intQualidade };
            enc.Frames.Add(BitmapFrame.Create(objFonte));
            using (MemoryStream ms = new MemoryStream())
            {
                enc.Save(ms);
                return ms.ToArray();
            }
        }

        private static void GravarImagemDePasta(string strFinal, byte[] arrDados)
        {
            string strParcial = strFinal + ".partial";
            if (File.Exists(strParcial)) { File.SetAttributes(strParcial, FileAttributes.Normal); File.Delete(strParcial); }
            File.WriteAllBytes(strParcial, arrDados);
            if (File.Exists(strFinal))
            {
                // Sem backup (decisão do Lorenzo, 20/11/2026): Folder.jpg e AlbumArtSmall.jpg
                // são só miniaturas derivadas da capa; a capa de verdade está nas faixas
                // e a troca fica registrada no ledger.
                File.SetAttributes(strFinal, FileAttributes.Normal);
                File.Replace(strParcial, strFinal, null);
            }
            else File.Move(strParcial, strFinal);
            File.SetAttributes(strFinal, FileAttributes.Hidden | FileAttributes.System);
        }

        private static string NomeDeBackup(string strArquivo)
        {
            string strBase = strArquivo + ".backup-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            string strBackup = strBase;
            for (int i = 2; File.Exists(strBackup); i++) strBackup = strBase + "-" + i;
            return strBackup;
        }

        // ------------------------------------------------------------ ledger

        /// <summary>O mesmo ARCA_Ledger.jsonl da AQUISIÇÃO, na raiz do acervo (Minhas Músicas).</summary>
        /// <summary>"07 Título.mp3": número novo e título novo, sem caracteres proibidos; a extensão do original.</summary>
        public static string NomeDaFaixa(TFaixaMp3 f)
        {
            string strTitulo = string.IsNullOrEmpty(f.strTituloNovo) ? Path.GetFileNameWithoutExtension(f.strCaminho) : f.strTituloNovo;
            strTitulo = System.Text.RegularExpressions.Regex.Replace(strTitulo, @"\s*[/\\:|]+\s*", " - ");   // "Perfeição/O Bêbado…" não vira "PerfeiçãoO Bêbado…"
            foreach (char c in Path.GetInvalidFileNameChars()) strTitulo = strTitulo.Replace(c.ToString(), "");
            strTitulo = System.Text.RegularExpressions.Regex.Replace(strTitulo, @"\s+", " ").Trim().TrimEnd('.');
            if (strTitulo.Length > 120) strTitulo = strTitulo.Substring(0, 120).Trim();
            string strNumero = f.intPosicaoNova >= 0 ? (f.intPosicaoNova + 1).ToString("00") + " " : "";
            return strNumero + strTitulo + Path.GetExtension(f.strCaminho).ToLowerInvariant();
        }

        /// <summary>Para ferramentas em lote, que rodam como SYSTEM (sem pasta Música): o ledger do acervo, explícito.</summary>
        public static string strLedgerForcado;

        public static string CaminhoDoLedger()
        {
            if (!string.IsNullOrEmpty(strLedgerForcado)) return strLedgerForcado;
            string strMusica = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (string.IsNullOrEmpty(strMusica))
                throw new InvalidOperationException("este usuário não tem pasta Música (rodando como SYSTEM?)");
            return Path.Combine(strMusica, "ARCA_Ledger.jsonl");
        }

        private static void EscreverLedger(string strPasta, TDadosAlbum a, List<Dictionary<string, object>> lstFaixas, List<string> lstArtwork)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["quando"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            d["evento"] = a.strEvento ?? "identificação MP3/WMA (WMP › Localizar Informações sobre o Álbum)";
            d["fonte"] = a.strFonte ?? "";
            d["artista"] = a.strArtistaAlbum ?? "";
            d["album"] = a.strAlbum ?? "";
            d["destino"] = strPasta;
            d["capa"] = a.arrCapa != null;
            if (a.arrCapa != null && !string.IsNullOrEmpty(a.strOrigemCapa)) d["origemCapa"] = a.strOrigemCapa;
            if (lstArtwork != null && lstArtwork.Count > 0) d["artwork"] = lstArtwork;
            d["faixas"] = lstFaixas;
            string strLinha = new JavaScriptSerializer().Serialize(d);
            File.AppendAllText(CaminhoDoLedger(), strLinha + "\n", new UTF8Encoding(false));
        }
    }
}
