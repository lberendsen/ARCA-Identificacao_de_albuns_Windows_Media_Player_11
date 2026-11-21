using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace ARCA.Identificar
{
    /// <summary>O "diálogo do scanner": tamanho e resolução, e a espera. O desenho está no JanelaDigitalizar.xaml.</summary>
    public partial class JanelaDigitalizar : Window
    {
        private sealed class TTamanho
        {
            public string strNome; public double dblL, dblA;
            public override string ToString() { return strNome; }
        }

        private static readonly TTamanho[] s_vetTamanhos =
        {
            new TTamanho { strNome = "Capa ou página de CD — 13 × 13 cm", dblL = 13, dblA = 13 },
            // A mesa tem 21,6 cm de largura: o encarte aberto (24 cm) vai em pé, ao longo do lado comprido.
            new TTamanho { strNome = "Encarte de CD aberto, em pé — 13,5 × 26 cm", dblL = 13.5, dblA = 26 },
            new TTamanho { strNome = "Mesa inteira — 21,6 × 29,7 cm", dblL = 21.6, dblA = 29.7 },
        };
        private static int s_intTamanho = 0;
        private static int s_intDpi = 300;

        public string strArquivo { get; private set; }
        public double dblDpi { get; private set; }

        private bool m_blnOcupado;

        public JanelaDigitalizar()
        {
            InitializeComponent();
            m_cmbTamanho.ItemsSource = s_vetTamanhos;
            m_cmbTamanho.SelectedIndex = s_intTamanho;
            m_cmbDpi.ItemsSource = new[] { 150, 300, 600 };
            m_cmbDpi.SelectedItem = s_intDpi;
            Closing += delegate(object s, System.ComponentModel.CancelEventArgs e) { if (m_blnOcupado) e.Cancel = true; };
        }

        private void AoCancelar(object sender, RoutedEventArgs e) { Close(); }

        private void AoDigitalizar(object sender, RoutedEventArgs e)
        {
            TTamanho t = (TTamanho)m_cmbTamanho.SelectedItem ?? s_vetTamanhos[0];
            int intDpi = m_cmbDpi.SelectedItem is int ? (int)m_cmbDpi.SelectedItem : 300;
            s_intTamanho = m_cmbTamanho.SelectedIndex;
            s_intDpi = intDpi;

            double dblArea = t.dblL * t.dblA * intDpi * intDpi / (13.0 * 13 * 300 * 300);
            m_blnOcupado = true;
            m_btnDigitalizar.IsEnabled = m_btnCancelar.IsEnabled = m_cmbTamanho.IsEnabled = m_cmbDpi.IsEnabled = false;
            Status(string.Format("Digitalizando na G7000… (uns {0} s; não abra a tampa)", (int)Math.Max(10, 35 * dblArea)), false);
            Programa.Registrar(string.Format("scanner: pedindo {0} × {1} cm a {2} dpi", t.dblL, t.dblA, intDpi));

            Task.Factory.StartNew(delegate
            {
                byte[] arrPng = null; double dblDpiLido = intDpi; Exception objErro = null;
                try { arrPng = ClienteCentral.Digitalizar(t.dblL, t.dblA, intDpi, out dblDpiLido); }
                catch (Exception ex) { objErro = ex; }
                Dispatcher.BeginInvoke((Action)delegate
                {
                    m_blnOcupado = false;
                    m_btnDigitalizar.IsEnabled = m_btnCancelar.IsEnabled = m_cmbTamanho.IsEnabled = m_cmbDpi.IsEnabled = true;
                    if (objErro != null)
                    {
                        Programa.Registrar("scanner: " + objErro.Message);
                        Status("Não deu: " + objErro.Message, true);
                        return;
                    }
                    try
                    {
                        string strArq = Path.Combine(Path.GetTempPath(), "ARCA_scan_" + Guid.NewGuid().ToString("N") + ".png");
                        File.WriteAllBytes(strArq, arrPng);
                        Programa.Registrar(string.Format("scanner: {0:N0} bytes a {1} dpi", arrPng.Length, dblDpiLido));
                        strArquivo = strArq;
                        dblDpi = dblDpiLido;
                        Close();
                    }
                    catch (Exception ex) { Status("Não consegui guardar a imagem: " + ex.Message, true); }
                });
            });
        }

        private void Status(string s, bool blnAlarme)
        {
            m_lblStatus.Text = s;
            m_lblStatus.Foreground = (TryFindResource(blnAlarme ? "Alarme" : "Texto") as Brush) ?? (blnAlarme ? Brushes.Red : Brushes.Black);
        }
    }
}
