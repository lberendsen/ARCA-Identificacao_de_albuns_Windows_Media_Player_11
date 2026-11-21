using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace ARCA.Identificar
{
    /// <summary>
    /// Divide uma imagem em duas por uma linha vertical — para quando duas
    /// páginas do encarte foram digitalizadas juntas, abertas na mesa.
    ///
    /// A linha começa no meio. Clicar ou arrastar sobre a imagem a leva até o
    /// ponteiro; as setas a movem 1 px (com Shift, 10 px). O corte é feito na
    /// imagem em resolução plena; a tela só mostra uma versão ajustada.
    ///
    /// Em XAML (editável no VS 2010).
    /// </summary>
    public partial class JanelaDivisaoImagem : Window
    {
        private const int intLadoMinimo = 32;       // px de imagem de cada lado

        private readonly BitmapSource m_objImagem;
        private int m_intLinhaX;                    // em pixels da imagem
        private double m_dblEscala = 1;             // tela / imagem
        private bool m_blnArrastando;

        /// <summary>A metade esquerda, em resolução plena, ou null se cancelado.</summary>
        public BitmapSource Esquerda { get; private set; }

        /// <summary>A metade direita, em resolução plena, ou null se cancelado.</summary>
        public BitmapSource Direita { get; private set; }

        public JanelaDivisaoImagem(BitmapSource objImagem, string strTitulo)
        {
            InitializeComponent();
            m_objImagem = objImagem;
            m_intLinhaX = objImagem.PixelWidth / 2;
            Title = strTitulo ?? "Dividir imagem";
            m_imgVista.Source = objImagem;
            PreviewKeyDown += AoTeclar;
            Desenhar();
        }

        private void AoVoltarAoMeio(object sender, RoutedEventArgs e) { MoverLinha(m_objImagem.PixelWidth / 2); }
        private void AoRedimensionarPalco(object sender, SizeChangedEventArgs e) { Diagramar(); }
        private void AoSoltar(object sender, MouseButtonEventArgs e) { Soltar(); }
        private void AoPerderMouse(object sender, MouseEventArgs e) { m_blnArrastando = false; }

        private void AoCancelar(object sender, RoutedEventArgs e)
        {
            Esquerda = null;
            Direita = null;
            Close();
        }

        private void AoDividir(object sender, RoutedEventArgs e)
        {
            int intAltura = m_objImagem.PixelHeight;
            CroppedBitmap objEsquerda = new CroppedBitmap(m_objImagem, new Int32Rect(0, 0, m_intLinhaX, intAltura));
            CroppedBitmap objDireita = new CroppedBitmap(m_objImagem, new Int32Rect(m_intLinhaX, 0, m_objImagem.PixelWidth - m_intLinhaX, intAltura));
            objEsquerda.Freeze();
            objDireita.Freeze();
            Esquerda = objEsquerda;
            Direita = objDireita;
            Close();
        }

        /// <summary>Leva a linha a uma coluna da imagem, sem deixar nenhum lado menor que o mínimo.</summary>
        private void MoverLinha(int intX)
        {
            m_intLinhaX = Math.Max(intLadoMinimo, Math.Min(m_objImagem.PixelWidth - intLadoMinimo, intX));
            Desenhar();
        }

        /// <summary>Ajusta a imagem ao espaço disponível, sem distorcer, e redesenha a linha.</summary>
        private void Diagramar()
        {
            double dblLargura = m_palco.ActualWidth, dblAltura = m_palco.ActualHeight;
            if (dblLargura <= 0 || dblAltura <= 0) return;
            m_dblEscala = Math.Min(dblLargura / m_objImagem.PixelWidth, dblAltura / m_objImagem.PixelHeight);
            m_imgVista.Width = m_objImagem.PixelWidth * m_dblEscala;
            m_imgVista.Height = m_objImagem.PixelHeight * m_dblEscala;
            Canvas.SetLeft(m_imgVista, (dblLargura - m_imgVista.Width) / 2);
            Canvas.SetTop(m_imgVista, (dblAltura - m_imgVista.Height) / 2);
            Desenhar();
        }

        private void Desenhar()
        {
            double dblEsquerda = Canvas.GetLeft(m_imgVista), dblTopo = Canvas.GetTop(m_imgVista);
            if (!double.IsNaN(dblEsquerda))
            {
                double dblX = dblEsquerda + m_intLinhaX * m_dblEscala;
                m_linha.X1 = dblX;
                m_linha.X2 = dblX;
                m_linha.Y1 = dblTopo;
                m_linha.Y2 = dblTopo + m_imgVista.Height;
            }
            m_lblTamanho.Text = string.Format("Esquerda: {0} x {2} px    Direita: {1} x {2} px",
                m_intLinhaX, m_objImagem.PixelWidth - m_intLinhaX, m_objImagem.PixelHeight);
        }

        private int ColunaDaImagem(Point objTela)
        {
            return (int)Math.Round((objTela.X - Canvas.GetLeft(m_imgVista)) / m_dblEscala);
        }

        private void AoApertar(object sender, MouseButtonEventArgs e)
        {
            m_blnArrastando = true;
            m_palco.CaptureMouse();
            MoverLinha(ColunaDaImagem(e.GetPosition(m_palco)));
        }

        private void AoMover(object sender, MouseEventArgs e)
        {
            if (!m_blnArrastando) return;
            if (e.LeftButton != MouseButtonState.Pressed) { Soltar(); return; }
            MoverLinha(ColunaDaImagem(e.GetPosition(m_palco)));
        }

        private void Soltar()
        {
            m_blnArrastando = false;
            if (m_palco.IsMouseCaptured) m_palco.ReleaseMouseCapture();
        }

        private void AoTeclar(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Left && e.Key != Key.Right) return;
            int intPasso = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
            MoverLinha(m_intLinhaX + (e.Key == Key.Left ? -intPasso : intPasso));
            e.Handled = true;
        }
    }
}
