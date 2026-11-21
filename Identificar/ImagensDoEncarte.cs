using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ARCA.Identificar
{
    /// <summary>
    /// O caminho comum de toda imagem de capa e encarte: obter (scanner ou
    /// arquivo), cortar, codificar. Versão do Identificar
    /// (que continua lá); os botões de cada página do encarte, que lá eram
    /// montados aqui, agora estão no JanelaEditorFaixas.xaml.
    /// </summary>
    public static class ImagensDoEncarte
    {
        /// <summary>Lado maior da capa embutida nas faixas. Scan de encarte tem milhares de px.</summary>
        public const int LadoMaximoCapaEmbutida = 1000;

        public const string FiltroImagens = "Imagens (*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff)|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff";

        /// <summary>
        /// Abre o scanner e devolve a imagem inteira, ou null. Em falha,
        /// strErro diz o motivo; em cancelamento, fica null. O temporário do
        /// scanner é apagado assim que lido.
        /// </summary>
        public static BitmapSource DoScanner(out string strErro)
        {
            strErro = null;
            string strArquivo;
            double dblDpi;
            try { strArquivo = ARCA.Interop.ScannerWia.Digitalizar(out dblDpi); }
            catch (InvalidOperationException ex) { strErro = ex.Message; return null; }
            if (strArquivo == null) return null;   // cancelado no diálogo do scanner

            BitmapSource objImagem = Abrir(strArquivo);
            try { File.Delete(strArquivo); } catch { }
            if (objImagem == null) { strErro = "O scanner entregou uma imagem que não consegui abrir."; return null; }
            return ComDpi(objImagem, dblDpi);
        }

        /// <summary>
        /// A resolução da imagem em pontos por polegada, ou 0 se o arquivo não
        /// diz. O WPF devolve 96 quando falta a informação, e nenhum scanner
        /// trabalha a 96 — então 96 conta como "não informada".
        /// </summary>
        public static double DpiDe(BitmapSource objImagem)
        {
            if (objImagem == null) return 0;
            double dblDpi = objImagem.DpiX;
            return dblDpi <= 0 || Math.Abs(dblDpi - 96) < 0.5 ? 0 : dblDpi;
        }

        /// <summary>
        /// Carimba na imagem a resolução que o scanner informou, quando o
        /// arquivo veio sem ela ou com outra. Os pixels não mudam; o corte e a
        /// gravação passam a saber o tamanho físico.
        /// </summary>
        private static BitmapSource ComDpi(BitmapSource objImagem, double dblDpi)
        {
            if (dblDpi < 50 || dblDpi > 4800 || Math.Abs(objImagem.DpiX - dblDpi) < 0.5) return objImagem;
            try
            {
                int intPasso = (objImagem.PixelWidth * objImagem.Format.BitsPerPixel + 7) / 8;
                byte[] arrPixels = new byte[intPasso * objImagem.PixelHeight];
                objImagem.CopyPixels(arrPixels, intPasso, 0);
                BitmapSource objNova = BitmapSource.Create(objImagem.PixelWidth, objImagem.PixelHeight, dblDpi, dblDpi,
                                                           objImagem.Format, objImagem.Palette, arrPixels, intPasso);
                objNova.Freeze();
                return objNova;
            }
            catch { return objImagem; }
        }

        public static BitmapSource DeArquivo(Window objDono, string strTitulo, out string strErro)
        {
            strErro = null;
            Microsoft.Win32.OpenFileDialog objDialogo = new Microsoft.Win32.OpenFileDialog { Title = strTitulo, Filter = FiltroImagens };
            if (objDialogo.ShowDialog(objDono) != true) return null;
            BitmapSource objImagem = Abrir(objDialogo.FileName);
            if (objImagem == null) strErro = "Não consegui abrir essa imagem.";
            return objImagem;
        }

        /// <summary>Lê a imagem inteira para a memória e solta o arquivo. Abre por FileStream: "#" e "%" no caminho não quebram.</summary>
        public static BitmapSource Abrir(string strArquivo)
        {
            try
            {
                using (FileStream objArq = new FileStream(strArquivo, FileMode.Open, FileAccess.Read, FileShare.Read))
                    return Abrir(objArq);
            }
            catch { return null; }
        }

        /// <summary>A imagem inteira, de bytes já na memória (JPEG pleno de uma página pendente).</summary>
        public static BitmapSource Abrir(byte[] arrBytes)
        {
            if (arrBytes == null || arrBytes.Length == 0) return null;
            try
            {
                using (MemoryStream objMem = new MemoryStream(arrBytes))
                    return Abrir(objMem);
            }
            catch { return null; }
        }

        private static BitmapSource Abrir(Stream objFluxo)
        {
            BitmapImage objImg = new BitmapImage();
            objImg.BeginInit();
            objImg.CacheOption = BitmapCacheOption.OnLoad;
            objImg.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            objImg.StreamSource = objFluxo;
            objImg.EndInit();
            objImg.Freeze();
            return objImg;
        }

        /// <summary>
        /// Abre a janela da linha vertical e põe as duas metades no lugar da
        /// página intIndice — a esquerda fica com a posição dela, a direita
        /// vem logo depois. false se cancelado ou se falhou (strErro diz o
        /// motivo).
        /// </summary>
        public static bool DividirPagina(Window objDono, List<TPaginaPendente> lstPaginas, int intIndice, string strTitulo, out string strErro)
        {
            byte[] arrEsquerda, arrDireita;
            if (!PedirDivisao(objDono, lstPaginas[intIndice].Imagem(), strTitulo, ".jpg", out arrEsquerda, out arrDireita, out strErro))
                return false;
            lstPaginas[intIndice] = new TPaginaPendente(arrEsquerda);
            lstPaginas.Insert(intIndice + 1, new TPaginaPendente(arrDireita));
            return true;
        }

        /// <summary>
        /// Abre a janela da linha vertical e devolve as duas metades já
        /// codificadas no formato de strExtensao. false se cancelado ou se
        /// falhou (strErro diz o motivo).
        /// </summary>
        public static bool PedirDivisao(Window objDono, BitmapSource objImagem, string strTitulo, string strExtensao,
                                        out byte[] arrEsquerda, out byte[] arrDireita, out string strErro)
        {
            arrEsquerda = arrDireita = null;
            strErro = null;
            if (objImagem == null) { strErro = "Não consegui abrir a imagem para dividir."; return false; }
            if (objImagem.PixelWidth < 64) { strErro = "A imagem é estreita demais para dividir."; return false; }

            JanelaDivisaoImagem objJanela = new JanelaDivisaoImagem(objImagem, strTitulo);
            objJanela.Owner = objDono;
            objJanela.ShowDialog();
            if (objJanela.Esquerda == null || objJanela.Direita == null) return false;

            arrEsquerda = CodificarComo(objJanela.Esquerda, strExtensao);
            arrDireita = CodificarComo(objJanela.Direita, strExtensao);
            if (arrEsquerda == null || arrDireita == null) { strErro = "Não consegui preparar as duas metades."; return false; }
            return true;
        }

        /// <summary>A imagem girada em graus no sentido horário (0 devolve a mesma).</summary>
        public static BitmapSource Girar(BitmapSource objImagem, int intGiro)
        {
            if (objImagem == null || intGiro == 0) return objImagem;
            TransformedBitmap objGirada = new TransformedBitmap(objImagem, new RotateTransform(intGiro));
            objGirada.Freeze();
            return objGirada;
        }

        /// <summary>
        /// Codifica no formato da extensão, para uma imagem do acervo
        /// regravada continuar sendo o que era: PNG, BMP e TIFF ficam sem
        /// perda; JPEG (e o que não se reconhece) sai como a plena, qualidade 95.
        /// </summary>
        public static byte[] CodificarComo(BitmapSource objImagem, string strExtensao)
        {
            BitmapEncoder objCodificador;
            switch ((strExtensao ?? "").ToLowerInvariant())
            {
                case ".png": objCodificador = new PngBitmapEncoder(); break;
                case ".bmp": objCodificador = new BmpBitmapEncoder(); break;
                case ".tif":
                case ".tiff": objCodificador = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw }; break;
                default: return CodificarPlena(objImagem);
            }
            try
            {
                objCodificador.Frames.Add(BitmapFrame.Create(objImagem));
                using (MemoryStream objSaida = new MemoryStream())
                {
                    objCodificador.Save(objSaida);
                    return objSaida.ToArray();
                }
            }
            catch { return null; }
        }

        /// <summary>Passa a imagem pela janela de corte. null se cancelado.</summary>
        public static BitmapSource Cortar(Window objDono, BitmapSource objImagem, string strTitulo, bool blnQuadrado)
        {
            JanelaCorteImagem objCorte = new JanelaCorteImagem(objImagem, strTitulo, blnQuadrado);
            objCorte.Owner = objDono;
            objCorte.ShowDialog();
            return objCorte.Resultado;
        }

        /// <summary>
        /// JPEG sem redução, qualidade 95: o original de preservação, que vai
        /// para Artwork\. Um scan de 300 dpi de página de 12 cm dá ~1400 px e
        /// cerca de 1 MB.
        /// </summary>
        public static byte[] CodificarPlena(BitmapSource objImagem)
        {
            return Codificar(objImagem, 95);
        }

        /// <summary>
        /// JPEG reduzido ao lado máximo, para embutir nas faixas. O WM/Picture
        /// declara JPEG, e a capa vai repetida em cada faixa: um scan de 4 MB
        /// em 20 arquivos seriam 80 MB a mais no acervo.
        /// </summary>
        public static byte[] CodificarReduzida(BitmapSource objImagem)
        {
            BitmapSource objFonte = objImagem;
            int intMaior = Math.Max(objImagem.PixelWidth, objImagem.PixelHeight);
            if (intMaior > LadoMaximoCapaEmbutida)
            {
                double dblEscala = (double)LadoMaximoCapaEmbutida / intMaior;
                objFonte = new TransformedBitmap(objImagem, new ScaleTransform(dblEscala, dblEscala));
            }
            return Codificar(objFonte, 90);
        }

        private static byte[] Codificar(BitmapSource objImagem, int intQualidade)
        {
            try
            {
                JpegBitmapEncoder objJpeg = new JpegBitmapEncoder { QualityLevel = intQualidade };
                objJpeg.Frames.Add(BitmapFrame.Create(objImagem));
                using (MemoryStream objSaida = new MemoryStream())
                {
                    objJpeg.Save(objSaida);
                    return objSaida.ToArray();
                }
            }
            catch { return null; }
        }

        /// <summary>Miniatura decodificada já pequena (não aloca o tamanho original). null se não for imagem.</summary>
        public static BitmapImage Miniatura(byte[] arrBytes, int intLargura)
        {
            if (arrBytes == null || arrBytes.Length == 0) return null;
            try
            {
                BitmapImage objImg = new BitmapImage();
                objImg.BeginInit();
                objImg.CacheOption = BitmapCacheOption.OnLoad;
                objImg.DecodePixelWidth = intLargura;
                objImg.StreamSource = new MemoryStream(arrBytes);
                objImg.EndInit();
                objImg.Freeze();
                return objImg;
            }
            catch { return null; }
        }

        public static BitmapImage Miniatura(string strArquivo, int intLargura)
        {
            try
            {
                BitmapImage objImg = new BitmapImage();
                using (FileStream objArq = new FileStream(strArquivo, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    objImg.BeginInit();
                    objImg.CacheOption = BitmapCacheOption.OnLoad;
                    objImg.DecodePixelWidth = intLargura;
                    objImg.StreamSource = objArq;
                    objImg.EndInit();
                }
                objImg.Freeze();
                return objImg;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Página do encarte já digitalizada, ainda na memória, antes de ir para
    /// o álbum.
    ///
    /// O giro fica pendente em vez de recodificar o JPEG a cada clique: girar
    /// quatro vezes não pode custar quatro gerações de compressão. Ele só é
    /// aplicado uma vez, em <see cref="JpegFinal"/>, e uma página que voltou
    /// à posição original sai byte a byte como veio do corte.
    /// </summary>
    public sealed class TPaginaPendente
    {
        /// <summary>JPEG pleno, como saiu do corte (ou da divisão).</summary>
        public readonly byte[] arrJpeg;

        /// <summary>Giro pendente, em graus no sentido horário: 0, 90, 180 ou 270.</summary>
        public int intGiro { get; private set; }

        public TPaginaPendente(byte[] arrJpeg)
        {
            this.arrJpeg = arrJpeg;
        }

        /// <summary>Gira 90° para a direita (intGraus positivo) ou para a esquerda (negativo).</summary>
        public void Girar(int intGraus)
        {
            intGiro = ((intGiro + intGraus) % 360 + 360) % 360;
        }

        /// <summary>A página inteira, com o giro aplicado. null se não abrir.</summary>
        public BitmapSource Imagem()
        {
            return Aplicar(ImagensDoEncarte.Abrir(arrJpeg));
        }

        /// <summary>Miniatura decodificada já pequena, com o giro aplicado.</summary>
        public BitmapSource Miniatura(int intLargura)
        {
            return Aplicar(ImagensDoEncarte.Miniatura(arrJpeg, intLargura));
        }

        /// <summary>O JPEG que vai para o álbum: o original se não houver giro, recodificado uma vez se houver.</summary>
        public byte[] JpegFinal()
        {
            if (intGiro == 0) return arrJpeg;
            BitmapSource objImagem = Imagem();
            return objImagem == null ? null : ImagensDoEncarte.CodificarPlena(objImagem);
        }

        private BitmapSource Aplicar(BitmapSource objImagem)
        {
            return ImagensDoEncarte.Girar(objImagem, intGiro);
        }
    }
}
