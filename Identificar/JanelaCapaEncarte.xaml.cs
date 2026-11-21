using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ARCA.Identificar
{
    /// <summary>
    /// Capa e encarte de um álbum que já está identificado, sem identificar de
    /// novo, em XAML (editável no VS
    /// 2010). As tags das faixas ficam como estão; a capa nova vai embutida em
    /// cada uma (e Folder.jpg/AlbumArtSmall.jpg ocultos), a plena para
    /// Artwork\capa.jpg, e as páginas para Artwork\encarte_NN.jpg — tudo pelo
    /// GravadorMp3 (cópia conferida, ledger).
    /// </summary>
    public partial class JanelaCapaEncarte : Window
    {
        private readonly List<TFaixaMp3> m_lstFaixas = new List<TFaixaMp3>();
        private readonly List<TPaginaPendente> m_lstEncarte = new List<TPaginaPendente>();
        private byte[] m_arrCapa, m_arrCapaPlena;
        private string m_strOrigemCapa;
        private bool m_blnGravando;

        public JanelaCapaEncarte(List<string> lstArquivos)
        {
            InitializeComponent();
            foreach (string s in lstArquivos.Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || TFaixaMp3.EhWma(f)))
            {
                try { m_lstFaixas.Add(TFaixaMp3.Ler(s)); } catch { }
            }
            string strPasta = m_lstFaixas.Count > 0 ? Path.GetDirectoryName(m_lstFaixas[0].strCaminho) : "";
            TFaixaMp3 f0 = m_lstFaixas.FirstOrDefault();
            m_lblAlbum.Text = f0 == null ? "nenhuma faixa" : string.Format("{0} — {1} · {2} faixa(s) · {3}",
                f0.strArtistaAlbum.Length > 0 ? f0.strArtistaAlbum : f0.strArtista, f0.strAlbum, m_lstFaixas.Count, strPasta);

            byte[] arrAtual = m_lstFaixas.Select(f => f.arrCapaEmbutida).FirstOrDefault(a => a != null);
            if (arrAtual == null && strPasta.Length > 0 && File.Exists(Path.Combine(strPasta, "Folder.jpg")))
                try { arrAtual = File.ReadAllBytes(Path.Combine(strPasta, "Folder.jpg")); } catch { }
            MostrarCapa(arrAtual, arrAtual != null ? "a capa que o álbum já tem" : "o álbum não tem capa");

            string strArt = Path.Combine(strPasta, "Artwork");
            int intJa = Directory.Exists(strArt) ? Directory.GetFiles(strArt, "*.jpg").Length + Directory.GetFiles(strArt, "*.png").Length : 0;
            m_lblJaTem.Text = intJa == 0 ? "Na pasta Artwork: nada ainda." : "Na pasta Artwork: " + intJa + " imagem(ns). As novas continuam a numeração; nada é apagado.";
            AtualizarEncarte();
            m_lblStatus.Text = "Digitalize a capa ou as páginas do encarte, ou escolha arquivos. Nada muda até \"Gravar no álbum\".";
            Closing += delegate(object s, System.ComponentModel.CancelEventArgs e) { if (m_blnGravando) e.Cancel = true; };
        }

        private void MostrarCapa(byte[] arr, string strNota)
        {
            BitmapImage img = ImagensDoEncarte.Miniatura(arr, 420);
            m_imgCapa.Source = img;
            m_lblSemCapa.Visibility = img != null ? Visibility.Collapsed : Visibility.Visible;
            m_lblCapa.Text = strNota;
        }

        private void Habilitar() { m_btnAplicar.IsEnabled = !m_blnGravando && (m_arrCapa != null || m_lstEncarte.Count > 0) && m_lstFaixas.Count > 0; }

        private void AoDigitalizarCapa(object sender, RoutedEventArgs e)
        {
            string strErro; BitmapSource img = ImagensDoEncarte.DoScanner(out strErro);
            if (strErro != null) m_lblStatus.Text = strErro;
            if (img != null) TrocarCapa(img, "digitalizada (G7000)");
        }

        private void AoCapaDeArquivo(object sender, RoutedEventArgs e)
        {
            string strErro; BitmapSource img = ImagensDoEncarte.DeArquivo(this, "Escolher a capa", out strErro);
            if (strErro != null) m_lblStatus.Text = strErro;
            if (img != null) TrocarCapa(img, "arquivo");
        }

        private void TrocarCapa(BitmapSource img, string strOrigem)
        {
            BitmapSource objCortada = ImagensDoEncarte.Cortar(this, img, "Cortar a capa", true);
            if (objCortada == null) return;
            m_arrCapa = ImagensDoEncarte.CodificarReduzida(objCortada);
            m_arrCapaPlena = ImagensDoEncarte.CodificarPlena(objCortada);
            if (m_arrCapa == null || m_arrCapaPlena == null) { m_lblStatus.Text = "Não consegui preparar a capa."; m_arrCapa = m_arrCapaPlena = null; return; }
            m_strOrigemCapa = strOrigem;
            MostrarCapa(m_arrCapa, string.Format("nova, {0} · {1} × {2} px", strOrigem, objCortada.PixelWidth, objCortada.PixelHeight));
            Habilitar();
        }

        private void AoDigitalizarPagina(object sender, RoutedEventArgs e)
        {
            string strErro; BitmapSource img = ImagensDoEncarte.DoScanner(out strErro);
            if (strErro != null) m_lblStatus.Text = strErro;
            if (img != null) NovaPagina(img);
        }

        private void AoPaginaDeArquivo(object sender, RoutedEventArgs e)
        {
            string strErro; BitmapSource img = ImagensDoEncarte.DeArquivo(this, "Escolher uma página do encarte", out strErro);
            if (strErro != null) m_lblStatus.Text = strErro;
            if (img != null) NovaPagina(img);
        }

        private void NovaPagina(BitmapSource img)
        {
            BitmapSource objCortada = ImagensDoEncarte.Cortar(this, img, string.Format("Cortar a página {0} do encarte", m_lstEncarte.Count + 1), false);
            if (objCortada == null) return;
            byte[] arr = ImagensDoEncarte.CodificarPlena(objCortada);
            if (arr == null) { m_lblStatus.Text = "Não consegui preparar a página."; return; }
            m_lstEncarte.Add(new TPaginaPendente(arr));
            AtualizarEncarte();
        }

        private void AtualizarEncarte()
        {
            m_lblEncarte.Text = m_lstEncarte.Count == 0 ? "Páginas novas do encarte" : "Páginas novas do encarte — " + m_lstEncarte.Count;
            m_lblSemPaginas.Visibility = m_lstEncarte.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            List<TMiniaturaPagina> lst = new List<TMiniaturaPagina>();
            for (int i = 0; i < m_lstEncarte.Count; i++)
                lst.Add(new TMiniaturaPagina { intIndice = i, objImagem = m_lstEncarte[i].Miniatura(168), strDica = "Página nova " + (i + 1) });
            m_lstPaginas.ItemsSource = lst;
            Habilitar();
        }

        private static int IndiceDe(object sender)
        {
            TMiniaturaPagina p = ((FrameworkElement)sender).DataContext as TMiniaturaPagina;
            return p == null ? -1 : p.intIndice;
        }

        private void AoGirarEsquerda(object sender, RoutedEventArgs e) { int i = IndiceDe(sender); if (i >= 0) { m_lstEncarte[i].Girar(-90); AtualizarEncarte(); } }
        private void AoGirarDireita(object sender, RoutedEventArgs e) { int i = IndiceDe(sender); if (i >= 0) { m_lstEncarte[i].Girar(90); AtualizarEncarte(); } }
        private void AoTirarPagina(object sender, RoutedEventArgs e) { int i = IndiceDe(sender); if (i >= 0) { m_lstEncarte.RemoveAt(i); AtualizarEncarte(); } }

        private void AoDividir(object sender, RoutedEventArgs e)
        {
            int i = IndiceDe(sender); if (i < 0) return;
            string strErro;
            if (ImagensDoEncarte.DividirPagina(this, m_lstEncarte, i, string.Format("Dividir a página {0} em duas", i + 1), out strErro)) AtualizarEncarte();
            if (strErro != null) m_lblStatus.Text = strErro;
        }

        private void AoFechar(object sender, RoutedEventArgs e) { Close(); }

        private void AoAplicar(object sender, RoutedEventArgs e)
        {
            List<byte[]> lstEncarte = new List<byte[]>();
            foreach (TPaginaPendente p in m_lstEncarte)
            {
                byte[] arr = p.JpegFinal();
                if (arr == null) { m_lblStatus.Text = "Não consegui preparar uma das páginas."; return; }
                lstEncarte.Add(arr);
            }
            // As tags ficam como estão: o álbum é o que as faixas já dizem.
            TFaixaMp3 f0 = m_lstFaixas[0];
            string strAA = f0.strArtistaAlbum.Length > 0 ? f0.strArtistaAlbum : f0.strArtista;
            foreach (TFaixaMp3 f in m_lstFaixas)
            {
                f.intPosicaoNova = f.intFaixaTag > 0 ? f.intFaixaTag - 1 : -1;
                f.strTituloNovo = f.strTitulo.Length > 0 ? f.strTitulo : Path.GetFileNameWithoutExtension(f.strCaminho);
                f.strArtistaNovo = string.Equals(f.strArtista, strAA, StringComparison.OrdinalIgnoreCase) ? "" : f.strArtista;
                f.strCompositorNovo = "";
                f.blnAplicar = true;
            }
            TDadosAlbum a = new TDadosAlbum
            {
                strArtistaAlbum = strAA, strAlbum = f0.strAlbum, strAno = f0.uintAno > 0 ? f0.uintAno.ToString() : "", strGenero = f0.strGenero,
                intTotalFaixas = Math.Max(m_lstFaixas.Count, m_lstFaixas.Max(f => f.intFaixaTag)),
                strFonte = "as tags que as faixas já têm", arrCapa = m_arrCapa, arrCapaPlena = m_arrCapaPlena, strOrigemCapa = m_strOrigemCapa,
                lstEncarte = lstEncarte.Count > 0 ? lstEncarte : null,
                strEvento = "capa e encarte de álbum já identificado (ARCA Identificar)"
            };
            bool blnPastaDeUmAlbum = m_lstFaixas.Select(f => Path.GetDirectoryName(f.strCaminho)).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
            m_blnGravando = true; Habilitar(); m_btnFechar.IsEnabled = false;
            m_lblStatus.Text = "gravando…";
            Task.Factory.StartNew(delegate
            {
                int intOk = 0; Exception objErro = null;
                try { intOk = GravadorMp3.Aplicar(m_lstFaixas, a, s => Programa.Registrar(s), delegate { }, null, blnPastaDeUmAlbum); }
                catch (Exception ex) { objErro = ex; }
                Dispatcher.BeginInvoke((Action)delegate
                {
                    m_blnGravando = false; m_btnFechar.IsEnabled = true;
                    if (objErro != null) { m_lblStatus.Text = "Falhou: " + objErro.Message; Habilitar(); return; }
                    m_lstEncarte.Clear(); m_arrCapa = m_arrCapaPlena = null; AtualizarEncarte();
                    m_lblStatus.Text = string.Format("{0} de {1} faixa(s) gravada(s), áudio conferido{2}.", intOk, m_lstFaixas.Count,
                                                     lstEncarte.Count > 0 ? "; " + lstEncarte.Count + " página(s) em Artwork" : "");
                });
            });
        }
    }
}
