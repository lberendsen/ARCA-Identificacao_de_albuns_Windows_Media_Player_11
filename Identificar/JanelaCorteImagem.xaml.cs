using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ARCA.Identificar
{
    /// <summary>
    /// Corte de imagem — para a capa que vem do scanner ou de uma foto.
    ///
    /// O scanner devolve a mesa inteira: a capa num canto, branco em volta.
    /// Aqui se escolhe o retângulo que interessa: arrastar por dentro move,
    /// pelos cantos redimensiona, fora desenha um novo. "Quadrado" vem ligado
    /// porque capa de CD é quadrada (12 x 12 cm).
    ///
    /// O corte é feito na imagem em resolução plena (CroppedBitmap); a tela só
    /// mostra uma versão ajustada ao espaço. A janela não comprime nada.
    ///
    /// Em XAML (editável no VS 2010).
    /// O desenho está no JanelaCorteImagem.xaml.
    /// </summary>
    public partial class JanelaCorteImagem : Window
    {
        private const double dblRaioAlca = 7;       // px de tela em volta de cada canto que contam como "pegar a alça"
        private const int intLadoMinimo = 32;       // px de imagem

        private enum Modo { Nenhum, Mover, Redimensionar, Desenhar }

        /// <summary>
        /// Moldura de tamanho físico fixo. Encarte de borda branca sobre a
        /// mesa branca do scanner não tem contorno a detectar; o tamanho, porém,
        /// é sabido: a página do livreto tem 12 x 12 cm, o encarte aberto 24 x 12.
        /// </summary>
        private enum Tamanho { Livre, Pagina, Aberto }
        private static readonly string[] arrNomesTamanho = { "Livre", "Página — 12 × 12 cm", "Encarte aberto — 24 × 12 cm" };
        private static readonly double[] arrLarguraMm = { 0, 120, 240 };
        private static readonly double[] arrAlturaMm = { 0, 120, 120 };

        // Lembrados entre uma página e outra: quem digitaliza um encarte passa várias páginas seguidas do mesmo jeito.
        private static Tamanho s_objUltimoTamanhoPagina = Tamanho.Livre;
        private static bool s_blnUltimaEmPe;
        private static double s_dblUltimoDpi = 300;

        private readonly bool m_blnPagina;           // corte de página (lembra o tamanho) ou de capa
        private Tamanho m_objTamanho = Tamanho.Livre;
        private bool m_blnMolduraEmPe;               // tamanho fixo com largura e altura trocadas

        private BitmapSource m_objImagem;
        private Rect m_objCorte;                     // em pixels da imagem
        private double m_dblEscala = 1;              // tela / imagem
        private readonly Rectangle[] m_arrAlcas;

        private Modo m_objModo = Modo.Nenhum;
        private Point m_objAncora;                   // canto fixo (redimensionar/desenhar) ou ponto de pega (mover), em px de imagem
        private Rect m_objCorteNoInicio;

        /// <summary>A imagem cortada em resolução plena, ou null se cancelado.</summary>
        public BitmapSource Resultado { get; private set; }

        public JanelaCorteImagem(BitmapSource objImagem, string strTitulo)
            : this(objImagem, strTitulo, true)
        {
        }

        /// <param name="blnQuadrado">
        /// Começa com "Quadrado" ligado (capa) ou desligado (página do encarte:
        /// a contracapa e o inlay traseiro são retangulares).
        /// </param>
        public JanelaCorteImagem(BitmapSource objImagem, string strTitulo, bool blnQuadrado)
        {
            InitializeComponent();
            m_objImagem = objImagem;
            m_arrAlcas = new[] { m_alca0, m_alca1, m_alca2, m_alca3 };
            Title = strTitulo ?? "Cortar imagem";

            m_chkQuadrado.IsChecked = blnQuadrado;
            m_chkQuadrado.Checked += delegate { AjustarAoQuadrado(); };

            m_blnPagina = !blnQuadrado;
            foreach (string strNome in arrNomesTamanho) m_cmbTamanho.Items.Add(strNome);

            // A resolução do próprio arquivo manda; sem ela, a última usada.
            double dblDpiImagem = ImagensDoEncarte.DpiDe(objImagem);
            m_txtDpi.Text = ((int)Math.Round(dblDpiImagem > 0 ? dblDpiImagem : s_dblUltimoDpi)).ToString();
            m_lblDpiOrigem.Text = dblDpiImagem > 0 ? "dpi" : "dpi (o arquivo não informa — confira)";
            m_txtDpi.TextChanged += delegate { if (m_objTamanho != Tamanho.Livre) AplicarTamanho(); };

            m_cmbTamanho.SelectedIndex = (int)(m_blnPagina ? s_objUltimoTamanhoPagina : Tamanho.Livre);
            m_blnMolduraEmPe = m_blnPagina && s_blnUltimaEmPe;
            m_objTamanho = (Tamanho)m_cmbTamanho.SelectedIndex;
            m_cmbTamanho.SelectionChanged += delegate { TrocarTamanho((Tamanho)m_cmbTamanho.SelectedIndex); };

            PreviewKeyDown += AoTeclar;

            if (m_objTamanho != Tamanho.Livre) m_chkQuadrado.IsChecked = false;
            m_chkQuadrado.IsEnabled = m_objTamanho == Tamanho.Livre;
            DefinirImagem(objImagem);
        }

        private void AoRedimensionarPalco(object sender, SizeChangedEventArgs e) { Diagramar(); }
        private void AoSoltar(object sender, MouseButtonEventArgs e) { Soltar(); }
        private void AoPerderMouse(object sender, MouseEventArgs e) { m_objModo = Modo.Nenhum; }
        private void AoGirarMoldura(object sender, RoutedEventArgs e) { GirarMoldura(); }

        private void AoSelecionarTudo(object sender, RoutedEventArgs e)
        {
            m_cmbTamanho.SelectedIndex = (int)Tamanho.Livre;   // "tudo" não tem tamanho fixo
            bool blnQuadrado = m_chkQuadrado.IsChecked == true;
            m_chkQuadrado.IsChecked = false;
            m_objCorte = new Rect(0, 0, m_objImagem.PixelWidth, m_objImagem.PixelHeight);
            if (blnQuadrado && m_objImagem.PixelWidth == m_objImagem.PixelHeight) m_chkQuadrado.IsChecked = true;
            Desenhar();
        }

        private void AoCancelar(object sender, RoutedEventArgs e)
        {
            Resultado = null;
            Close();
        }

        private void AoUsar(object sender, RoutedEventArgs e)
        {
            Int32Rect objR = new Int32Rect((int)Math.Round(m_objCorte.X), (int)Math.Round(m_objCorte.Y),
                                           (int)Math.Round(m_objCorte.Width), (int)Math.Round(m_objCorte.Height));
            if (m_blnPagina) { s_objUltimoTamanhoPagina = m_objTamanho; s_blnUltimaEmPe = m_blnMolduraEmPe; }
            double dblDpi = DpiDigitado();
            if (dblDpi > 0) s_dblUltimoDpi = dblDpi;
            objR.Width = Math.Min(objR.Width, m_objImagem.PixelWidth - objR.X);
            objR.Height = Math.Min(objR.Height, m_objImagem.PixelHeight - objR.Y);
            CroppedBitmap objCortada = new CroppedBitmap(m_objImagem, objR);
            objCortada.Freeze();
            Resultado = objCortada;
            Close();
        }

        // ===============================================================
        /// <summary>
        /// Troca a imagem (abertura ou giro) e recomeça o corte. Primeiro
        /// tenta achar a capa sobre o fundo do scanner; não achando, quadrado
        /// central de 90% do lado menor.
        /// </summary>
        private void DefinirImagem(BitmapSource objImagem)
        {
            m_objImagem = objImagem;
            m_imgVista.Source = objImagem;

            Rect? objAchado = DetectarConteudo(objImagem);
            if (objAchado.HasValue)
            {
                m_objCorte = objAchado.Value;
                if (m_chkQuadrado.IsChecked == true) AjustarAoQuadradoMaior();
            }
            else
            {
                double dblLado = Math.Min(objImagem.PixelWidth, objImagem.PixelHeight) * 0.9;
                m_objCorte = new Rect((objImagem.PixelWidth - dblLado) / 2, (objImagem.PixelHeight - dblLado) / 2, dblLado, dblLado);
                if (m_chkQuadrado.IsChecked != true)
                    m_objCorte = new Rect(objImagem.PixelWidth * 0.05, objImagem.PixelHeight * 0.05,
                                         objImagem.PixelWidth * 0.9, objImagem.PixelHeight * 0.9);
            }
            // Com tamanho fixo, a detecção só serve de ponto de partida: a
            // moldura nasce centrada no que foi achado (o miolo impresso da
            // página, quando a borda é branca).
            if (m_objTamanho != Tamanho.Livre) AjustarMolduraFixa();
            Diagramar();
        }

        // ===============================================================
        private void TrocarTamanho(Tamanho objNovo)
        {
            m_objTamanho = objNovo;
            if (objNovo == Tamanho.Livre)
            {
                // Livre mantém a moldura onde está, agora com as alças: é
                // assim que se afina um encarte um pouco maior ou menor.
                m_chkQuadrado.IsEnabled = true;
                Desenhar();
                return;
            }
            m_chkQuadrado.IsChecked = false;
            m_chkQuadrado.IsEnabled = false;
            AplicarTamanho();
        }

        private void AplicarTamanho()
        {
            AjustarMolduraFixa();
            Desenhar();
        }

        /// <summary>
        /// Dá à moldura o tamanho físico escolhido, convertido pela resolução,
        /// mantendo o centro atual. Se não couber (resolução errada, scan
        /// menor que o encarte), reduz mantendo a proporção.
        /// </summary>
        private void AjustarMolduraFixa()
        {
            double dblDpi = DpiDigitado();
            if (dblDpi <= 0) return;   // ainda digitando
            int i = (int)m_objTamanho;
            double dblL = arrLarguraMm[i] / 25.4 * dblDpi, dblA = arrAlturaMm[i] / 25.4 * dblDpi;
            if (m_blnMolduraEmPe) { double dblT = dblL; dblL = dblA; dblA = dblT; }
            double dblFator = Math.Min(1.0, Math.Min(m_objImagem.PixelWidth / dblL, m_objImagem.PixelHeight / dblA));
            dblL *= dblFator;
            dblA *= dblFator;
            Point objCentro = new Point(m_objCorte.X + m_objCorte.Width / 2, m_objCorte.Y + m_objCorte.Height / 2);
            m_objCorte = Limitado(new Rect(objCentro.X - dblL / 2, objCentro.Y - dblA / 2, dblL, dblA));
        }

        /// <summary>
        /// Troca largura e altura da moldura em volta do centro. Com tamanho
        /// fixo, a orientação fica guardada (vale também ao mudar o dpi); no
        /// Livre, se a moldura girada não couber, encolhe na mesma proporção.
        /// </summary>
        private void GirarMoldura()
        {
            if (m_objTamanho != Tamanho.Livre)
            {
                m_blnMolduraEmPe = !m_blnMolduraEmPe;
                AplicarTamanho();
                return;
            }
            double dblL = m_objCorte.Height, dblA = m_objCorte.Width;
            double dblFator = Math.Min(1.0, Math.Min(m_objImagem.PixelWidth / dblL, m_objImagem.PixelHeight / dblA));
            dblL *= dblFator;
            dblA *= dblFator;
            Point objCentro = new Point(m_objCorte.X + m_objCorte.Width / 2, m_objCorte.Y + m_objCorte.Height / 2);
            m_objCorte = Limitado(new Rect(objCentro.X - dblL / 2, objCentro.Y - dblA / 2, dblL, dblA));
            Desenhar();
        }

        /// <summary>O retângulo empurrado para dentro da imagem, sem mudar o tamanho.</summary>
        private Rect Limitado(Rect objR)
        {
            double dblX = Math.Max(0, Math.Min(m_objImagem.PixelWidth - objR.Width, objR.X));
            double dblY = Math.Max(0, Math.Min(m_objImagem.PixelHeight - objR.Height, objR.Y));
            return new Rect(dblX, dblY, objR.Width, objR.Height);
        }

        /// <summary>A resolução do campo, ou 0 se o que está lá não serve.</summary>
        private double DpiDigitado()
        {
            int intDpi;
            return int.TryParse(m_txtDpi.Text.Trim(), out intDpi) && intDpi >= 50 && intDpi <= 4800 ? intDpi : 0;
        }

        /// <summary>As setas movem a moldura 1 px da imagem; com Shift, 10. Fora dos campos de texto e da lista.</summary>
        private void AoTeclar(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox || e.OriginalSource is ComboBox || e.OriginalSource is ComboBoxItem) return;
            double dblDx = 0, dblDy = 0;
            double dblPasso = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
            switch (e.Key)
            {
                case Key.Left: dblDx = -dblPasso; break;
                case Key.Right: dblDx = dblPasso; break;
                case Key.Up: dblDy = -dblPasso; break;
                case Key.Down: dblDy = dblPasso; break;
                default: return;
            }
            m_objCorte = Limitado(new Rect(m_objCorte.X + dblDx, m_objCorte.Y + dblDy, m_objCorte.Width, m_objCorte.Height));
            Desenhar();
            e.Handled = true;
        }

        /// <summary>
        /// Onde a capa está sobre a mesa do scanner. Trabalha numa cópia
        /// reduzida (~300 px): a cor do fundo sai da mediana dos quatro cantos,
        /// e conta como conteúdo a linha ou coluna em que mais de 2% dos pixels
        /// se afastam dela. Ruído e poeira não chegam a 2%; a capa passa
        /// muito. null quando não há fundo claro a separar (foto já recortada,
        /// imagem toda preenchida) — aí vale o corte padrão.
        /// </summary>
        private static Rect? DetectarConteudo(BitmapSource objImagem)
        {
            try
            {
                double dblReducao = Math.Min(1.0, 300.0 / Math.Max(objImagem.PixelWidth, objImagem.PixelHeight));
                BitmapSource objPequena = new FormatConvertedBitmap(
                    new TransformedBitmap(objImagem, new ScaleTransform(dblReducao, dblReducao)), PixelFormats.Bgr32, null, 0);
                int intL = objPequena.PixelWidth, intA = objPequena.PixelHeight;
                if (intL < 16 || intA < 16) return null;
                int[] arrPx = new int[intL * intA];
                objPequena.CopyPixels(arrPx, intL * 4, 0);

                // Fundo: mediana dos quatro cantos (um canto pode cair na capa).
                int[] arrCantos = { arrPx[0], arrPx[intL - 1], arrPx[(intA - 1) * intL], arrPx[intA * intL - 1] };
                int[] arrCanal = new int[3];
                for (int c = 0; c < 3; c++)
                {
                    int[] arrV = new int[4];
                    for (int k = 0; k < 4; k++) arrV[k] = (arrCantos[k] >> (8 * c)) & 0xFF;
                    Array.Sort(arrV);
                    arrCanal[c] = (arrV[1] + arrV[2]) / 2;
                }

                int[] arrPorLinha = new int[intA], arrPorColuna = new int[intL];
                for (int y = 0; y < intA; y++)
                    for (int x = 0; x < intL; x++)
                    {
                        int intP = arrPx[y * intL + x];
                        int intDif = Math.Abs((intP & 0xFF) - arrCanal[0]) + Math.Abs(((intP >> 8) & 0xFF) - arrCanal[1])
                                   + Math.Abs(((intP >> 16) & 0xFF) - arrCanal[2]);
                        if (intDif > 60) { arrPorLinha[y]++; arrPorColuna[x]++; }
                    }

                int intY0 = Primeiro(arrPorLinha, intL / 50), intY1 = Ultimo(arrPorLinha, intL / 50);
                int intX0 = Primeiro(arrPorColuna, intA / 50), intX1 = Ultimo(arrPorColuna, intA / 50);
                if (intY0 < 0 || intX0 < 0) return null;

                Rect objR = new Rect(intX0 / dblReducao, intY0 / dblReducao,
                                     (intX1 - intX0 + 1) / dblReducao, (intY1 - intY0 + 1) / dblReducao);
                objR.Intersect(new Rect(0, 0, objImagem.PixelWidth, objImagem.PixelHeight));
                double dblFracao = objR.Width * objR.Height / ((double)objImagem.PixelWidth * objImagem.PixelHeight);
                // Quase a imagem toda: não havia fundo. Pequeno demais: era sujeira.
                if (dblFracao > 0.92 || objR.Width < intLadoMinimo * 2 || objR.Height < intLadoMinimo * 2) return null;
                return objR;
            }
            catch { return null; }
        }

        private static int Primeiro(int[] arrContagem, int intMinimo)
        {
            for (int i = 0; i < arrContagem.Length; i++) if (arrContagem[i] > Math.Max(1, intMinimo)) return i;
            return -1;
        }

        private static int Ultimo(int[] arrContagem, int intMinimo)
        {
            for (int i = arrContagem.Length - 1; i >= 0; i--) if (arrContagem[i] > Math.Max(1, intMinimo)) return i;
            return -1;
        }

        /// <summary>
        /// Quadrado pelo lado MAIOR em volta do conteúdo detectado: a capa
        /// inteira fica dentro, mesmo que a detecção tenha cortado uma borda
        /// clara. Limitado à imagem.
        /// </summary>
        private void AjustarAoQuadradoMaior()
        {
            double dblLado = Math.Min(Math.Max(m_objCorte.Width, m_objCorte.Height),
                                      Math.Min(m_objImagem.PixelWidth, m_objImagem.PixelHeight));
            Point objCentro = new Point(m_objCorte.X + m_objCorte.Width / 2, m_objCorte.Y + m_objCorte.Height / 2);
            double dblX = Math.Max(0, Math.Min(m_objImagem.PixelWidth - dblLado, objCentro.X - dblLado / 2));
            double dblY = Math.Max(0, Math.Min(m_objImagem.PixelHeight - dblLado, objCentro.Y - dblLado / 2));
            m_objCorte = new Rect(dblX, dblY, dblLado, dblLado);
        }

        /// <summary>Ajusta a imagem ao espaço disponível, sem distorcer, e redesenha o corte.</summary>
        private void Diagramar()
        {
            double dblLargura = m_palco.ActualWidth, dblAltura = m_palco.ActualHeight;
            if (dblLargura <= 0 || dblAltura <= 0 || m_objImagem == null) return;
            m_dblEscala = Math.Min(dblLargura / m_objImagem.PixelWidth, dblAltura / m_objImagem.PixelHeight);
            m_imgVista.Width = m_objImagem.PixelWidth * m_dblEscala;
            m_imgVista.Height = m_objImagem.PixelHeight * m_dblEscala;
            Canvas.SetLeft(m_imgVista, (dblLargura - m_imgVista.Width) / 2);
            Canvas.SetTop(m_imgVista, (dblAltura - m_imgVista.Height) / 2);
            Desenhar();
        }

        private Point ParaTela(Point objP)
        {
            return new Point(Canvas.GetLeft(m_imgVista) + objP.X * m_dblEscala, Canvas.GetTop(m_imgVista) + objP.Y * m_dblEscala);
        }

        private Point ParaImagem(Point objP)
        {
            double dblX = (objP.X - Canvas.GetLeft(m_imgVista)) / m_dblEscala;
            double dblY = (objP.Y - Canvas.GetTop(m_imgVista)) / m_dblEscala;
            return new Point(Math.Max(0, Math.Min(m_objImagem.PixelWidth, dblX)), Math.Max(0, Math.Min(m_objImagem.PixelHeight, dblY)));
        }

        private void Desenhar()
        {
            if (double.IsNaN(Canvas.GetLeft(m_imgVista))) return;
            Point objA = ParaTela(m_objCorte.TopLeft), objB = ParaTela(m_objCorte.BottomRight);
            Rect objNaTela = new Rect(objA, objB);
            Rect objImagemNaTela = new Rect(Canvas.GetLeft(m_imgVista), Canvas.GetTop(m_imgVista), m_imgVista.Width, m_imgVista.Height);

            GeometryGroup objGrupo = new GeometryGroup { FillRule = FillRule.EvenOdd };
            objGrupo.Children.Add(new RectangleGeometry(objImagemNaTela));
            objGrupo.Children.Add(new RectangleGeometry(objNaTela));
            m_veu.Data = objGrupo;

            Canvas.SetLeft(m_moldura, objNaTela.X);
            Canvas.SetTop(m_moldura, objNaTela.Y);
            m_moldura.Width = objNaTela.Width;
            m_moldura.Height = objNaTela.Height;

            Point[] arrCantos = { objNaTela.TopLeft, objNaTela.TopRight, objNaTela.BottomRight, objNaTela.BottomLeft };
            for (int i = 0; i < 4; i++)
            {
                Canvas.SetLeft(m_arrAlcas[i], arrCantos[i].X - m_arrAlcas[i].Width / 2);
                Canvas.SetTop(m_arrAlcas[i], arrCantos[i].Y - m_arrAlcas[i].Height / 2);
            }

            m_lblTamanho.Text = string.Format("Corte: {0} x {1} px   (imagem {2} x {3})",
                (int)Math.Round(m_objCorte.Width), (int)Math.Round(m_objCorte.Height),
                m_objImagem.PixelWidth, m_objImagem.PixelHeight);
            double dblDpi = DpiDigitado();
            if (dblDpi > 0)
                m_lblTamanho.Text += string.Format("   ≈ {0:0.0} x {1:0.0} cm", m_objCorte.Width / dblDpi * 2.54, m_objCorte.Height / dblDpi * 2.54);
        }

        // ===============================================================
        private void AoApertar(object sender, MouseButtonEventArgs e)
        {
            Point objTela = e.GetPosition(m_palco);
            Point objImg = ParaImagem(objTela);

            // Tamanho fixo: a moldura só se move. Clicar fora dela a traz
            // centrada no clique, e o arraste continua dali.
            if (m_objTamanho != Tamanho.Livre)
            {
                if (!m_objCorte.Contains(objImg))
                {
                    m_objCorte = Limitado(new Rect(objImg.X - m_objCorte.Width / 2, objImg.Y - m_objCorte.Height / 2,
                                                  m_objCorte.Width, m_objCorte.Height));
                    Desenhar();
                }
                m_objCorteNoInicio = m_objCorte;
                m_objModo = Modo.Mover;
                m_objAncora = objImg;
                m_palco.CaptureMouse();
                return;
            }
            m_objCorteNoInicio = m_objCorte;

            // Canto mais próximo, dentro do raio da alça: redimensiona com o
            // canto oposto fixo.
            Point[] arrCantos = { m_objCorte.TopLeft, m_objCorte.TopRight, m_objCorte.BottomRight, m_objCorte.BottomLeft };
            for (int i = 0; i < 4; i++)
            {
                Point objCantoTela = ParaTela(arrCantos[i]);
                if (Math.Abs(objCantoTela.X - objTela.X) <= dblRaioAlca && Math.Abs(objCantoTela.Y - objTela.Y) <= dblRaioAlca)
                {
                    m_objModo = Modo.Redimensionar;
                    m_objAncora = arrCantos[(i + 2) % 4];
                    m_palco.CaptureMouse();
                    return;
                }
            }

            if (m_objCorte.Contains(objImg))
            {
                m_objModo = Modo.Mover;
                m_objAncora = objImg;
            }
            else
            {
                m_objModo = Modo.Desenhar;
                m_objAncora = objImg;
            }
            m_palco.CaptureMouse();
        }

        private void AoMover(object sender, MouseEventArgs e)
        {
            Point objTela = e.GetPosition(m_palco);
            if (m_objModo == Modo.Nenhum)
            {
                m_palco.Cursor = CursorPara(objTela);
                return;
            }
            if (e.LeftButton != MouseButtonState.Pressed) { Soltar(); return; }

            Point objImg = ParaImagem(objTela);
            if (m_objModo == Modo.Mover)
            {
                double dblX = m_objCorteNoInicio.X + (objImg.X - m_objAncora.X);
                double dblY = m_objCorteNoInicio.Y + (objImg.Y - m_objAncora.Y);
                dblX = Math.Max(0, Math.Min(m_objImagem.PixelWidth - m_objCorteNoInicio.Width, dblX));
                dblY = Math.Max(0, Math.Min(m_objImagem.PixelHeight - m_objCorteNoInicio.Height, dblY));
                m_objCorte = new Rect(dblX, dblY, m_objCorteNoInicio.Width, m_objCorteNoInicio.Height);
            }
            else
            {
                Rect objNovo = RetanguloEntre(m_objAncora, objImg);
                if (objNovo.Width >= intLadoMinimo && objNovo.Height >= intLadoMinimo) m_objCorte = objNovo;
            }
            Desenhar();
        }

        private void Soltar()
        {
            m_objModo = Modo.Nenhum;
            if (m_palco.IsMouseCaptured) m_palco.ReleaseMouseCapture();
        }

        /// <summary>
        /// Retângulo do canto fixo até o ponteiro. No modo quadrado, o lado é
        /// o maior dos dois deslocamentos, limitado ao que cabe na imagem a
        /// partir do canto fixo, na direção em que o ponteiro foi.
        /// </summary>
        private Rect RetanguloEntre(Point objFixo, Point objMovel)
        {
            if (m_chkQuadrado.IsChecked != true) return new Rect(objFixo, objMovel);

            double dblDx = objMovel.X - objFixo.X, dblDy = objMovel.Y - objFixo.Y;
            double dblSinalX = dblDx < 0 ? -1 : 1, dblSinalY = dblDy < 0 ? -1 : 1;
            double dblLado = Math.Max(Math.Abs(dblDx), Math.Abs(dblDy));
            double dblCabeX = dblSinalX > 0 ? m_objImagem.PixelWidth - objFixo.X : objFixo.X;
            double dblCabeY = dblSinalY > 0 ? m_objImagem.PixelHeight - objFixo.Y : objFixo.Y;
            dblLado = Math.Min(dblLado, Math.Min(dblCabeX, dblCabeY));
            return new Rect(objFixo, new Point(objFixo.X + dblSinalX * dblLado, objFixo.Y + dblSinalY * dblLado));
        }

        /// <summary>Ao ligar "Quadrado", o corte atual vira quadrado pelo lado menor, mantendo o centro.</summary>
        private void AjustarAoQuadrado()
        {
            double dblLado = Math.Min(m_objCorte.Width, m_objCorte.Height);
            Point objCentro = new Point(m_objCorte.X + m_objCorte.Width / 2, m_objCorte.Y + m_objCorte.Height / 2);
            double dblX = Math.Max(0, Math.Min(m_objImagem.PixelWidth - dblLado, objCentro.X - dblLado / 2));
            double dblY = Math.Max(0, Math.Min(m_objImagem.PixelHeight - dblLado, objCentro.Y - dblLado / 2));
            m_objCorte = new Rect(dblX, dblY, dblLado, dblLado);
            Desenhar();
        }

        private Cursor CursorPara(Point objTela)
        {
            if (m_objTamanho != Tamanho.Livre) return Cursors.SizeAll;
            Point[] arrCantos = { m_objCorte.TopLeft, m_objCorte.TopRight, m_objCorte.BottomRight, m_objCorte.BottomLeft };
            for (int i = 0; i < 4; i++)
            {
                Point objC = ParaTela(arrCantos[i]);
                if (Math.Abs(objC.X - objTela.X) <= dblRaioAlca && Math.Abs(objC.Y - objTela.Y) <= dblRaioAlca)
                    return i % 2 == 0 ? Cursors.SizeNWSE : Cursors.SizeNESW;
            }
            return m_objCorte.Contains(ParaImagem(objTela)) ? Cursors.SizeAll : Cursors.Cross;
        }
    }
}
