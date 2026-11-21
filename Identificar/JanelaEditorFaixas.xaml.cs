using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using ARCA.Modelo;

namespace ARCA.Identificar
{
    /// <summary>Uma página na tira do encarte: o que o modelo "PaginaDoEncarte" mostra.</summary>
    public sealed class TMiniaturaPagina
    {
        public int intIndice { get; set; }
        public BitmapSource objImagem { get; set; }
        public string strDica { get; set; }
    }

    /// <summary>
    /// Catalogação à mão: para o disco que nenhum catálogo conhece (CD
    /// independente, vendido em apresentação, fora do Discogs e do Apple Music).
    /// O encarte na mão da pessoa é a única fonte.
    ///
    /// O número e a duração de cada faixa vêm dos arquivos e não se editam: são
    /// a régua contra a qual se confere o encarte. O resto é digitado. A janela
    /// só devolve um TCandidatoIdentificacao; gravar é o mesmo caminho da
    /// escolha entre Apple Music e Discogs.
    ///
    /// Em XAML (editável no VS 2010).
    /// </summary>
    public partial class JanelaEditorFaixas : Window
    {
        public sealed class LinhaFaixaManual
        {
            public int Numero { get; set; }
            public string Duracao { get; set; }
            public string Titulo { get; set; }
            public string Interprete { get; set; }
            public string Compositor { get; set; }
        }

        private readonly List<LinhaFaixaManual> m_lstLinhas = new List<LinhaFaixaManual>();
        private byte[] m_arrCapa;            // reduzida, para embutir nas faixas
        private byte[] m_arrCapaPlena;       // sem redução, para Artwork\capa.jpg
        private readonly List<TPaginaPendente> m_lstEncarte = new List<TPaginaPendente>();
        private bool m_blnAplicado;
        private string m_strEstadoInicial;

        /// <summary>O que foi catalogado, ou null se a pessoa cancelou.</summary>
        public TCandidatoIdentificacao Resultado { get; private set; }

        /// <param name="lstDuracoesDisco">Duração de cada faixa, em segundos (-1 se desconhecida).</param>
        /// <param name="lstTitulosAtuais">Títulos já conhecidos, para não redigitar; pode ser null.</param>
        /// <param name="lstCompositoresAtuais">Compositores já conhecidos; pode ser null.</param>
        public JanelaEditorFaixas(List<int> lstDuracoesDisco, string strArtista, string strAlbum,
                                  List<string> lstTitulosAtuais, List<string> lstCompositoresAtuais, byte[] arrCapaAtual)
        {
            InitializeComponent();

            int intTotal = 0;
            for (int i = 0; i < lstDuracoesDisco.Count; i++)
            {
                if (lstDuracoesDisco[i] > 0) intTotal += lstDuracoesDisco[i];
                string strTitulo = lstTitulosAtuais != null && i < lstTitulosAtuais.Count ? lstTitulosAtuais[i] : null;
                // "Faixa 07" é nome provisório, não um título.
                if (strTitulo != null && Regex.IsMatch(strTitulo.Trim(), @"^Faixa\s*\d+$", RegexOptions.IgnoreCase)) strTitulo = null;
                m_lstLinhas.Add(new LinhaFaixaManual
                {
                    Numero = i + 1,
                    Duracao = lstDuracoesDisco[i] > 0 ? Formato.Tempo(lstDuracoesDisco[i]) : "?",
                    Titulo = strTitulo ?? "",
                    Interprete = "",
                    Compositor = lstCompositoresAtuais != null && i < lstCompositoresAtuais.Count ? (lstCompositoresAtuais[i] ?? "") : ""
                });
            }
            m_lblResumo.Text = string.Format("{0} faixas · {1}. Número e duração vêm dos arquivos — use-os para conferir o encarte.",
                                             lstDuracoesDisco.Count, Formato.Tempo(intTotal));
            m_txtArtista.Text = strArtista ?? "";
            m_txtAlbum.Text = strAlbum ?? "";
            m_lblAviso.Text = "";
            m_grade.ItemsSource = m_lstLinhas;

            DefinirCapa(arrCapaAtual);
            AtualizarEncarte();
            m_strEstadoInicial = Instantaneo();
            Closing += AoFechar;
        }

        private void AoCancelar(object sender, RoutedEventArgs e) { Close(); }

        // =============================================================== títulos colados

        /// <summary>
        /// Uma faixa por linha, na ordem. Linhas vazias são puladas; número,
        /// ponto, traço e parêntese no começo ("01 - ", "1.", "(3)") saem.
        /// Linhas a mais que faixas são avisadas, não descartadas em silêncio.
        /// </summary>
        private void AoColarTitulos(object sender, RoutedEventArgs e)
        {
            string strTexto = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            List<string> lstTitulos = new List<string>();
            foreach (string strLinha in strTexto.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None))
            {
                string strLimpa = Regex.Replace(strLinha, @"^\s*[\(\[]?\d{1,3}[\)\]]?\s*[-–.:)]?\s*", "").Trim();
                if (strLimpa.Length > 0) lstTitulos.Add(strLimpa);
            }
            if (lstTitulos.Count == 0)
            {
                m_lblAviso.Text = "A área de transferência não tem texto. Copie a lista de títulos (uma faixa por linha) e tente de novo.";
                return;
            }

            m_grade.CommitEdit(DataGridEditingUnit.Row, true);
            for (int i = 0; i < m_lstLinhas.Count && i < lstTitulos.Count; i++) m_lstLinhas[i].Titulo = lstTitulos[i];
            m_grade.Items.Refresh();

            m_lblAviso.Text = lstTitulos.Count == m_lstLinhas.Count ? ""
                : string.Format("Colei {0} títulos para {1} faixas — confira {2}.", lstTitulos.Count, m_lstLinhas.Count,
                                lstTitulos.Count > m_lstLinhas.Count ? "as linhas que sobraram" : "as faixas que ficaram sem título");
        }

        // =============================================================== capa e encarte

        private void AoEscolherCapa(object sender, RoutedEventArgs e)
        {
            string strErro;
            BitmapSource objImagem = ImagensDoEncarte.DeArquivo(this, "Escolher a capa", out strErro);
            m_lblAviso.Text = strErro ?? "";
            if (objImagem != null) CortarCapa(objImagem);
        }

        private void AoDigitalizarCapa(object sender, RoutedEventArgs e)
        {
            string strErro;
            BitmapSource objImagem = ImagensDoEncarte.DoScanner(out strErro);
            m_lblAviso.Text = strErro ?? "";
            if (objImagem != null) CortarCapa(objImagem);
        }

        private void AoTirarCapa(object sender, RoutedEventArgs e)
        {
            DefinirCapa(null);
            m_arrCapaPlena = null;
        }

        private void AoDigitalizarPagina(object sender, RoutedEventArgs e)
        {
            string strErro;
            BitmapSource objImagem = ImagensDoEncarte.DoScanner(out strErro);
            m_lblAviso.Text = strErro ?? "";
            if (objImagem != null) CortarPagina(objImagem);
        }

        private void AoPaginaDeArquivo(object sender, RoutedEventArgs e)
        {
            string strErro;
            BitmapSource objImagem = ImagensDoEncarte.DeArquivo(this, "Escolher uma página do encarte", out strErro);
            m_lblAviso.Text = strErro ?? "";
            if (objImagem != null) CortarPagina(objImagem);
        }

        /// <summary>Capa: corte quadrado; reduzida a 1000 px para as faixas, plena para Artwork\capa.jpg.</summary>
        private void CortarCapa(BitmapSource objImagem)
        {
            BitmapSource objCortada = ImagensDoEncarte.Cortar(this, objImagem, "Cortar a capa", true);
            if (objCortada == null) return;
            byte[] arrReduzida = ImagensDoEncarte.CodificarReduzida(objCortada);
            byte[] arrPlena = ImagensDoEncarte.CodificarPlena(objCortada);
            if (arrReduzida == null || arrPlena == null) { m_lblAviso.Text = "Não consegui preparar a capa."; return; }
            m_lblAviso.Text = "";
            DefinirCapa(arrReduzida);
            m_arrCapaPlena = arrPlena;
        }

        /// <summary>Página do encarte: corte livre, guardada só em resolução plena.</summary>
        private void CortarPagina(BitmapSource objImagem)
        {
            BitmapSource objCortada = ImagensDoEncarte.Cortar(this, objImagem,
                string.Format("Cortar a página {0} do encarte", m_lstEncarte.Count + 1), false);
            if (objCortada == null) return;
            byte[] arrPlena = ImagensDoEncarte.CodificarPlena(objCortada);
            if (arrPlena == null) { m_lblAviso.Text = "Não consegui preparar a página."; return; }
            m_lblAviso.Text = "";
            m_lstEncarte.Add(new TPaginaPendente(arrPlena));
            AtualizarEncarte();
        }

        /// <summary>Remonta a tira de miniaturas do encarte (o desenho de cada uma está no XAML).</summary>
        private void AtualizarEncarte()
        {
            m_lblEncarte.Text = m_lstEncarte.Count == 0 ? "Encarte"
                : string.Format("Encarte — {0} página{1}", m_lstEncarte.Count, m_lstEncarte.Count == 1 ? "" : "s");
            m_lblSemPaginas.Visibility = m_lstEncarte.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            List<TMiniaturaPagina> lst = new List<TMiniaturaPagina>();
            for (int i = 0; i < m_lstEncarte.Count; i++)
                lst.Add(new TMiniaturaPagina { intIndice = i, objImagem = m_lstEncarte[i].Miniatura(168), strDica = "Página " + (i + 1) });
            m_lstPaginas.ItemsSource = lst;
        }

        /// <summary>A página do botão clicado (o DataContext do botão é a TMiniaturaPagina dele).</summary>
        private static int IndiceDe(object sender)
        {
            TMiniaturaPagina p = ((FrameworkElement)sender).DataContext as TMiniaturaPagina;
            return p == null ? -1 : p.intIndice;
        }

        private void AoGirarEsquerda(object sender, RoutedEventArgs e)
        {
            int i = IndiceDe(sender); if (i < 0) return;
            m_lstEncarte[i].Girar(-90); AtualizarEncarte();
        }

        private void AoGirarDireita(object sender, RoutedEventArgs e)
        {
            int i = IndiceDe(sender); if (i < 0) return;
            m_lstEncarte[i].Girar(90); AtualizarEncarte();
        }

        private void AoTirarPagina(object sender, RoutedEventArgs e)
        {
            int i = IndiceDe(sender); if (i < 0) return;
            m_lstEncarte.RemoveAt(i); AtualizarEncarte();
        }

        private void AoDividir(object sender, RoutedEventArgs e)
        {
            int i = IndiceDe(sender); if (i < 0) return;
            string strErro;
            bool blnDividiu = ImagensDoEncarte.DividirPagina(this, m_lstEncarte, i,
                string.Format("Dividir a página {0} do encarte em duas", i + 1), out strErro);
            m_lblAviso.Text = strErro ?? "";
            if (blnDividiu) AtualizarEncarte();
        }

        private void DefinirCapa(byte[] arrBytes)
        {
            m_arrCapa = arrBytes != null && arrBytes.Length > 0 ? arrBytes : null;
            BitmapImage objImg = ImagensDoEncarte.Miniatura(m_arrCapa, 300);
            m_imgCapa.Source = objImg;
            m_lblSemCapa.Visibility = objImg != null ? Visibility.Collapsed : Visibility.Visible;
        }

        // =============================================================== aplicar

        private void AoAplicar(object sender, RoutedEventArgs e)
        {
            m_grade.CommitEdit(DataGridEditingUnit.Row, true);

            string strArtista = m_txtArtista.Text.Trim();
            string strAlbum = m_txtAlbum.Text.Trim();
            string strAno = m_txtAno.Text.Trim();

            List<int> lstSemTitulo = new List<int>();
            foreach (LinhaFaixaManual objL in m_lstLinhas)
                if (string.IsNullOrWhiteSpace(objL.Titulo)) lstSemTitulo.Add(objL.Numero);

            if (strArtista.Length == 0 || strAlbum.Length == 0) { m_lblAviso.Text = "Preencha o artista do álbum e o álbum."; return; }
            if (strAno.Length > 0 && !Regex.IsMatch(strAno, @"^\d{4}$")) { m_lblAviso.Text = "O ano tem de ter quatro dígitos (ou ficar vazio)."; return; }
            // Todas as faixas com título: um disco meio catalogado entraria com "Faixa 07" no meio dos nomes certos.
            if (lstSemTitulo.Count > 0)
            {
                m_lblAviso.Text = "Falta o título da(s) faixa(s) " + string.Join(", ", lstSemTitulo.ConvertAll(n => n.ToString()).ToArray()) + ".";
                return;
            }

            // O giro pendente de cada página é aplicado aqui, uma vez só.
            List<byte[]> lstEncarte = null;
            if (m_lstEncarte.Count > 0)
            {
                lstEncarte = new List<byte[]>();
                foreach (TPaginaPendente objPagina in m_lstEncarte)
                {
                    byte[] arrPagina = objPagina.JpegFinal();
                    if (arrPagina == null)
                    {
                        m_lblAviso.Text = string.Format("Não consegui preparar a página {0} do encarte.", lstEncarte.Count + 1);
                        return;
                    }
                    lstEncarte.Add(arrPagina);
                }
            }

            TCandidatoIdentificacao objC = new TCandidatoIdentificacao
            {
                strFonte = "encarte (digitação)",
                strArtista = strArtista,
                strAlbum = strAlbum,
                strAno = strAno.Length > 0 ? strAno : null,
                strGenero = m_txtGenero.Text.Trim().Length > 0 ? m_txtGenero.Text.Trim() : null,
                arrCapa = m_arrCapa,
                arrCapaPlena = m_arrCapaPlena,
                lstEncarte = lstEncarte
            };
            bool blnTemCompositor = false;
            List<string> lstCompositores = new List<string>();
            foreach (LinhaFaixaManual objL in m_lstLinhas)
            {
                objC.lstTitulos.Add(objL.Titulo.Trim());
                // Intérprete vazio = o artista do álbum (o gravador já faz isso).
                objC.lstArtistasFaixas.Add((objL.Interprete ?? "").Trim());
                string strComp = (objL.Compositor ?? "").Trim();
                if (strComp.Length > 0) blnTemCompositor = true;
                lstCompositores.Add(strComp);
                objC.lstDuracoes.Add(-1);
            }
            objC.lstCompositores = blnTemCompositor ? lstCompositores : null;

            Resultado = objC;
            m_blnAplicado = true;
            Close();
        }

        /// <summary>Digitar 20 títulos e perder num clique errado não pode acontecer.</summary>
        private void AoFechar(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (m_blnAplicado) return;
            m_grade.CommitEdit(DataGridEditingUnit.Row, true);
            if (Instantaneo() == m_strEstadoInicial) return;
            MessageBoxResult objResp = MessageBox.Show(this, "Há dados digitados que ainda não foram aplicados. Descartar?",
                "Catalogar à mão", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (objResp != MessageBoxResult.Yes) e.Cancel = true;
        }

        /// <summary>Tudo o que é editável numa string só, para saber se algo mudou.</summary>
        private string Instantaneo()
        {
            System.Text.StringBuilder objSb = new System.Text.StringBuilder();
            objSb.Append(m_txtArtista.Text).Append('\u0001').Append(m_txtAlbum.Text).Append('\u0001')
                 .Append(m_txtAno.Text).Append('\u0001').Append(m_txtGenero.Text).Append('\u0001')
                 .Append(m_arrCapa == null ? 0 : m_arrCapa.Length).Append('\u0001').Append(m_lstEncarte.Count);
            // Girar ou dividir uma página também é mudança a não perder.
            foreach (TPaginaPendente objPagina in m_lstEncarte)
                objSb.Append('\u0003').Append(objPagina.arrJpeg.Length).Append('/').Append(objPagina.intGiro);
            foreach (LinhaFaixaManual objL in m_lstLinhas)
                objSb.Append('\u0002').Append(objL.Titulo).Append('\u0001').Append(objL.Interprete).Append('\u0001').Append(objL.Compositor);
            return objSb.ToString();
        }
    }
}
