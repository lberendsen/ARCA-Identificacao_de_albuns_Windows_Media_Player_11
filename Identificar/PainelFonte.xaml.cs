using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ARCA.Modelo;

namespace ARCA.Identificar
{
    /// <summary>Uma faixa na coluna de uma fonte: o que o modelo "LinhaDaFaixa" do PainelFonte.xaml mostra.</summary>
    public sealed class TLinhaFonte
    {
        public string strNumero { get; set; }
        public string strTitulo { get; set; }
        public string strArtista { get; set; }
        public string strCompositor { get; set; }
        public Visibility visArtista { get { return string.IsNullOrEmpty(strArtista) ? Visibility.Collapsed : Visibility.Visible; } }
        public Visibility visCompositor { get { return string.IsNullOrEmpty(strCompositor) ? Visibility.Collapsed : Visibility.Visible; } }
        public string strDuracao { get; set; }
        public string strDif { get; set; }
        public string strDicaDif { get; set; }
        public Brush objCorDif { get; set; }
        public FontWeight objPesoDif { get; set; }
    }

    /// <summary>
    /// Uma coluna da escolha da fonte. O desenho está no PainelFonte.xaml; aqui
    /// só a conferência com as faixas do disco (contagem, durações, total).
    /// </summary>
    public partial class PainelFonte : UserControl
    {
        /// <summary>Até quantos segundos de diferença uma faixa ainda "confere".</summary>
        public const int TOLERANCIA_SEGUNDOS = 10;

        /// <summary>A pessoa clicou em "Usar os dados do …".</summary>
        public event EventHandler Usar;

        public TCandidatoIdentificacao objCandidato { get; private set; }

        public PainelFonte()
        {
            InitializeComponent();
        }

        private void AoUsar(object sender, RoutedEventArgs e)
        {
            if (Usar != null && objCandidato != null) Usar(this, EventArgs.Empty);
        }

        /// <summary>Preenche a coluna. objC null = a fonte não achou nada.</summary>
        public void Mostrar(string strFonte, TCandidatoIdentificacao objC, List<int> lstDuracoesDisco)
        {
            Mostrar(strFonte, objC, lstDuracoesDisco, null);
        }

        /// <summary>
        /// Para cada faixa da fonte, a faixa do disco que é ela (ou -1). Disco inteiro
        /// (mesma quantidade, sem títulos): pela posição, como num CD.
        /// Faixas avulsas do WMP (1 de 12, 7 de 14…): pelo menor custo entre duração
        /// e título — o mesmo casamento da gravação. Antes era sempre pela posição, e
        /// uma faixa 3 sozinha aparecia contra a 1, com a 3 "sem faixa" (Stanley
        /// Clarke, 20/11/2026).
        /// </summary>
        public static int[] Parear(TCandidatoIdentificacao objC, List<int> lstDuracoesDisco, List<string> lstTitulosDisco)
        {
            int m = objC.lstTitulos.Count, n = lstDuracoesDisco.Count;
            int[] vet = Enumerable.Repeat(-1, m).ToArray();
            if (n == m) { for (int j = 0; j < m; j++) vet[j] = j; return vet; }
            List<Tuple<double, int, int>> lstCustos = new List<Tuple<double, int, int>>();
            for (int i = 0; i < n; i++)
                for (int j = 0; j < m; j++)
                {
                    int intDurFonte = j < objC.lstDuracoes.Count ? objC.lstDuracoes[j] : -1;
                    double dblDur = intDurFonte > 0 && lstDuracoesDisco[i] > 0 ? Math.Abs(intDurFonte - lstDuracoesDisco[i]) : 30;
                    string strTit = lstTitulosDisco != null && i < lstTitulosDisco.Count ? lstTitulosDisco[i] : "";
                    double dblSim = string.IsNullOrEmpty(strTit) ? 0 : DiscogsClient.CalcularSimilaridadeTexto(strTit, objC.lstTitulos[j]);
                    lstCustos.Add(Tuple.Create(Math.Min(dblDur, 120) + (1 - dblSim) * 60, i, j));
                }
            bool[] vetUsada = new bool[n];
            foreach (Tuple<double, int, int> t in lstCustos.OrderBy(x => x.Item1))
            {
                if (t.Item1 > 90) break;
                if (vetUsada[t.Item2] || vet[t.Item3] >= 0) continue;
                vet[t.Item3] = t.Item2;
                vetUsada[t.Item2] = true;
            }
            return vet;
        }

        public void Mostrar(string strFonte, TCandidatoIdentificacao objC, List<int> lstDuracoesDisco, List<string> lstTitulosDisco)
        {
            objCandidato = objC;
            m_lblFonte.Text = strFonte;
            if (objC == null)
            {
                m_lblNada.Visibility = Visibility.Visible;
                m_btnUsar.Visibility = m_gradeIdentidade.Visibility = m_rolagemFaixas.Visibility = Visibility.Collapsed;
                return;
            }
            Brush objTexto = Cor("Texto", Brushes.Black), objFraco = Cor("TextoFraco", Brushes.Gray), objAlarme = Cor("Alarme", Brushes.Red);

            m_btnUsar.Content = "Usar os dados do " + objC.strFonte;

            BitmapImage objImagem = AbrirCapa(objC.arrCapa);
            m_imgCapa.Source = objImagem;
            m_lblSemCapa.Visibility = objImagem != null ? Visibility.Collapsed : Visibility.Visible;
            // A miniatura é decodificada reduzida; o tamanho que interessa (o que vai para o arquivo) é o original.
            m_imgCapa.ToolTip = objImagem != null ? TamanhoOriginal(objC.arrCapa) + " · " + (objC.arrCapa.Length / 1024) + " KB" : null;

            m_lblArtista.Text = objC.strArtista ?? "";
            m_lblAlbum.Text = objC.strAlbum ?? "";
            List<string> lstAnoGenero = new List<string>();
            if (!string.IsNullOrEmpty(objC.strAno)) lstAnoGenero.Add(objC.strAno);
            if (!string.IsNullOrEmpty(objC.strGenero)) lstAnoGenero.Add(objC.strGenero);
            m_lblAnoGenero.Text = string.Join(" · ", lstAnoGenero.ToArray());
            m_lblAnoGenero.Visibility = lstAnoGenero.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            m_lblDetalhe.Text = objC.strDetalhe ?? "";
            m_lblDetalhe.Visibility = string.IsNullOrEmpty(objC.strDetalhe) ? Visibility.Collapsed : Visibility.Visible;

            // A conferência que mais pesa: a contagem de faixas. Com faixas avulsas
            // (menos que o álbum), o que importa é se TODAS as suas acharam par.
            int intQtd = objC.lstTitulos.Count, intQtdDisco = lstDuracoesDisco.Count;
            int[] vetPar = Parear(objC, lstDuracoesDisco, lstTitulosDisco);
            int intPareadas = vetPar.Count(x => x >= 0);
            bool blnAvulsas = intQtdDisco > 0 && intQtdDisco < intQtd;
            bool blnConfere = intQtdDisco == 0 || intQtd == intQtdDisco || (blnAvulsas && intPareadas == intQtdDisco);
            m_lblContagem.Text = intQtdDisco == 0 ? string.Format("{0} faixas", intQtd)
                : intQtd == intQtdDisco ? string.Format("{0} faixas — confere com o disco", intQtd)
                : blnAvulsas ? (intPareadas == intQtdDisco
                                ? string.Format("{0} faixas — {1}", intQtd, intQtdDisco == 1 ? "a sua está nele" : "as suas " + intQtdDisco + " estão nele")
                                : string.Format("{0} faixas — das suas {1}, só {2} estão nele", intQtd, intQtdDisco, intPareadas))
                : string.Format("{0} faixas — o disco tem {1}", intQtd, intQtdDisco);
            m_lblContagem.Foreground = blnConfere ? objTexto : objAlarme;

            int intTotalFonte = 0, intTotalDisco = 0;
            bool blnTotalCompleto = intQtd > 0 && !blnAvulsas;   // total só faz sentido com o disco inteiro
            foreach (int d in objC.lstDuracoes) { if (d > 0) intTotalFonte += d; else blnTotalCompleto = false; }
            foreach (int d in lstDuracoesDisco) if (d > 0) intTotalDisco += d;
            if (blnTotalCompleto && intTotalDisco > 0)
            {
                // A soma tolera a mesma folga por faixa que cada linha tolera.
                bool blnTotalConfere = Math.Abs(intTotalDisco - intTotalFonte) <= TOLERANCIA_SEGUNDOS * Math.Max(1, intQtd);
                m_lblTotal.Text = string.Format("Total {0} (disco {1})", Formato.Tempo(intTotalFonte), Formato.Tempo(intTotalDisco));
                m_lblTotal.Foreground = blnTotalConfere ? objFraco : objAlarme;
                m_lblTotal.FontWeight = blnTotalConfere ? FontWeights.Normal : FontWeights.SemiBold;
            }
            else m_lblTotal.Visibility = Visibility.Collapsed;

            List<TLinhaFonte> lst = new List<TLinhaFonte>();
            for (int i = 0; i < objC.lstTitulos.Count; i++)
            {
                string strArtistaFaixa = i < objC.lstArtistasFaixas.Count ? objC.lstArtistasFaixas[i] : null;
                if (string.Equals(strArtistaFaixa, objC.strArtista, StringComparison.OrdinalIgnoreCase)) strArtistaFaixa = null;
                string strCompositor = objC.lstCompositores != null && i < objC.lstCompositores.Count ? objC.lstCompositores[i] : null;
                int intDur = i < objC.lstDuracoes.Count ? objC.lstDuracoes[i] : -1;
                int intDisco = vetPar[i] >= 0 ? lstDuracoesDisco[vetPar[i]] : -1;

                TLinhaFonte l = new TLinhaFonte
                {
                    strNumero = (i + 1).ToString(),
                    strTitulo = objC.lstTitulos[i],
                    strArtista = strArtistaFaixa,
                    strCompositor = string.IsNullOrEmpty(strCompositor) ? null : "comp. " + strCompositor,
                    strDuracao = intDur > 0 ? Formato.Tempo(intDur) : "?",
                    objCorDif = objFraco, objPesoDif = FontWeights.Normal,
                    strDicaDif = intDisco > 0 ? "Faixa no disco: " + Formato.Tempo(intDisco) : null
                };
                // Faixa do álbum que você não tem: com avulsas é normal (cinza, "—"); com o disco inteiro, é alarme.
                if (vetPar[i] < 0 && blnAvulsas) { l.strDif = "—"; l.strDicaDif = "Esta faixa do álbum não está entre as que você abriu."; }
                else if (intDisco <= 0) { l.strDif = lstDuracoesDisco.Count > 0 ? "sem faixa" : ""; l.objCorDif = objAlarme; }
                else if (intDur <= 0) l.strDif = "?";
                else
                {
                    int intDif = intDisco - intDur;
                    bool blnOk = Math.Abs(intDif) <= TOLERANCIA_SEGUNDOS;
                    l.strDif = (intDif >= 0 ? "+" : "−") + Math.Abs(intDif) + " s";
                    l.objCorDif = blnOk ? objFraco : objAlarme;
                    l.objPesoDif = blnOk ? FontWeights.Normal : FontWeights.SemiBold;
                }
                lst.Add(l);
            }
            m_lstFaixas.ItemsSource = lst;
        }

        private Brush Cor(string strChave, Brush objPadrao) { return (TryFindResource(strChave) as Brush) ?? objPadrao; }

        /// <summary>Largura x altura da imagem original, lidas sem decodificar os pixels.</summary>
        private static string TamanhoOriginal(byte[] arrBytes)
        {
            try
            {
                BitmapDecoder objDec = BitmapDecoder.Create(new MemoryStream(arrBytes), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                return objDec.Frames[0].PixelWidth + " x " + objDec.Frames[0].PixelHeight + " px";
            }
            catch { return "tamanho desconhecido"; }
        }

        /// <summary>A capa já reduzida (a moldura tem 160 px). null se não houver, ou se os bytes não forem imagem.</summary>
        private static BitmapImage AbrirCapa(byte[] arrBytes)
        {
            if (arrBytes == null || arrBytes.Length == 0) return null;
            try
            {
                BitmapImage objImg = new BitmapImage();
                objImg.BeginInit();
                objImg.CacheOption = BitmapCacheOption.OnLoad;
                objImg.DecodePixelWidth = 320;
                objImg.StreamSource = new MemoryStream(arrBytes);
                objImg.EndInit();
                objImg.Freeze();
                return objImg;
            }
            catch { return null; }
        }
    }
}
