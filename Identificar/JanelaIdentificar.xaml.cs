using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ARCA.Central;
using ARCA.Modelo;


namespace ARCA.Identificar
{
    /// <summary>
    /// "Localizar Informações sobre o Álbum", do jeito do ARCA.
    ///
    /// À esquerda, o álbum como está no disco: faixas, durações, tags atuais.
    /// À direita, o que a pessoa sabe (artista, álbum, ou o link exato). O ARCA
    /// Central, no notebook, consulta Apple Music e Discogs; a escolha é feita
    /// lado a lado na mesma janela da AQUISIÇÃO (JanelaEscolhaFonte), comparando
    /// as durações. Depois a proposta aparece faixa a faixa, editável no nível
    /// do álbum, e só "Aplicar às faixas" grava — com o áudio conferido.
    ///
    /// O desenho está no JanelaIdentificar.xaml; aqui só o comportamento.
    /// </summary>
    public partial class JanelaIdentificar : Window
    {
        private const int TOLERANCIA_SEGUNDOS = 10;

        private readonly List<TFaixaMp3> m_lstFaixas = new List<TFaixaMp3>();
        private readonly List<string> m_lstIgnorados = new List<string>();
        private TCandidatoIdentificacao m_objEscolhido;
        /// <summary>Enquanto grava, a janela não fecha: fechar mataria a gravação no meio do álbum.</summary>
        private bool m_blnGravando;
        /// <summary>A capa que o álbum já tem (embutida ou Folder.jpg), para o "Catalogar à mão" partir dela.</summary>
        private byte[] m_arrCapaAtual;
        /// <summary>De onde veio a capa do escolhido, para o ledger.</summary>
        private string m_strOrigemCapa;

        /// <summary>
        /// Chamada pela separação do Álbum Desconhecido: se não for null, as faixas
        /// gravadas vão para &lt;raiz&gt;\Artista\Ano - Álbum, com o nome "NN Título".
        /// </summary>
        public string strRaizParaMover { get; set; }

        /// <summary>Ao mover, a pasta do disco leva o ano ("1994 - Álbum")? O acervo de CDs ripados é Artista\Álbum, como o WMP faz.</summary>
        public bool blnAnoNaPasta { get; set; }

        /// <summary>Começar a identificação sozinha ao abrir (o ARCA Ripar: as faixas trazem o índice do CD).</summary>
        public bool blnIdentificarAoAbrir { get; set; }

        /// <summary>O que o ledger registra como origem da gravação (null = a identificação pelo WMP).</summary>
        public string strEventoAoAplicar { get; set; }

        public JanelaIdentificar(List<string> lstArquivos)
        {
            InitializeComponent();
            blnAnoNaPasta = true;
            LerFaixas(lstArquivos);
            PreencherCabecalho();

            string strArtistaAtual = MaisComum(m_lstFaixas.Select(f => f.strArtistaAlbum.Length > 0 ? f.strArtistaAlbum : f.strArtista));
            string strAlbumAtual = MaisComum(m_lstFaixas.Select(f => f.strAlbum));
            m_txtArtista.Text = EhGenerico(strArtistaAtual) ? "" : strArtistaAtual;
            m_txtAlbum.Text = EhGenerico(strAlbumAtual) ? "" : strAlbumAtual;
            m_grade.ItemsSource = m_lstFaixas;
            foreach (string s in m_lstIgnorados) Log("ignorado: " + s);

            // No XAML o painel do escolhido aparece (para dar para desenhá-lo); aqui some até haver escolha.
            m_painelEscolhido.Visibility = Visibility.Collapsed;
            m_lblFonte.Text = m_lblCapaNova.Text = m_lblGeneros.Text = "";

            m_btnAccurateRip.IsEnabled = m_lstFaixas.Any(f => f.blnWma) && m_blnAlgumIndice;
            if (!m_btnAccurateRip.IsEnabled) m_btnAccurateRip.ToolTip = "Só para WMA ripado pelo WMP: estas faixas não guardam o índice do CD (WM/MCDI).";
            m_btnCapaEncarte.IsEnabled = m_btnConteudo.IsEnabled = m_lstFaixas.Count > 0;
            if (m_lstFaixas.Count == 0)
            {
                Status("Nenhuma faixa MP3 ou WMA recebida." + (m_lstIgnorados.Count > 0 ? " (" + m_lstIgnorados.Count + " arquivo(s) de outro formato.)" : ""), true);
                m_btnIdentificar.IsEnabled = false;
                m_btnCatalogar.IsEnabled = false;
            }
            else if (m_strMcdi != null)
                Status("As faixas guardam o índice do CD: basta clicar em Identificar (nome e álbum são opcionais). Nada muda nos arquivos até \"Aplicar às faixas\".", false);
            else Status("Diga o que sabe e clique em Identificar — ou, para disco que nenhum catálogo conhece, Catalogar à mão. Nada muda nos arquivos até \"Aplicar às faixas\".", false);
            Loaded += delegate
            {
                m_txtArtista.Focus(); m_txtArtista.SelectAll();
                if (blnIdentificarAoAbrir && m_btnIdentificar.IsEnabled) AoIdentificar(this, null);
            };
            Closing += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                if (!m_blnGravando) return;
                e.Cancel = true;
                MessageBox.Show(this, "Espere a gravação terminar. Fechar agora deixaria o álbum pela metade.",
                                "ARCA — Identificar álbum", MessageBoxButton.OK, MessageBoxImage.Information);
            };
        }

        private void AoFechar(object sender, RoutedEventArgs e) { Close(); }

        private List<string> Caminhos() { return m_lstFaixas.Select(f => f.strCaminho).ToList(); }

        private void AoCapaEncarte(object sender, RoutedEventArgs e)
        {
            JanelaCapaEncarte j = new JanelaCapaEncarte(Caminhos()) { Owner = this };
            j.ShowDialog();
        }

        private void AoConteudo(object sender, RoutedEventArgs e)
        {
            JanelaConteudoFaixas j = new JanelaConteudoFaixas(Caminhos()) { Owner = this };
            j.ShowDialog();
        }

        private void AoAccurateRip(object sender, RoutedEventArgs e)
        {
            JanelaConferenciaAlbum j = new JanelaConferenciaAlbum(Caminhos()) { Owner = this };
            j.ShowDialog();
        }

        private void AoDiscografia(object sender, RoutedEventArgs e)
        {
            JanelaDiscografia j = new JanelaDiscografia(m_lstFaixas.Select(f => f.strCaminho).ToList());
            j.Owner = this;
            j.ShowDialog();
        }

        /// <summary>A capa que o álbum já tem e o resumo, na faixa de cima.</summary>
        private void PreencherCabecalho()
        {
            byte[] arrCapaAtual = m_lstFaixas.Select(f => f.arrCapaEmbutida).FirstOrDefault(a => a != null);
            if (arrCapaAtual == null && m_lstFaixas.Count > 0)
            {
                string strFolder = Path.Combine(Path.GetDirectoryName(m_lstFaixas[0].strCaminho), "Folder.jpg");
                if (File.Exists(strFolder)) { try { arrCapaAtual = File.ReadAllBytes(strFolder); } catch { } }
            }
            m_arrCapaAtual = arrCapaAtual;
            BitmapImage objCapa = AbrirImagem(arrCapaAtual, 168);
            m_imgCapaAtual.Source = objCapa;
            m_lblSemCapa.Visibility = objCapa != null ? Visibility.Collapsed : Visibility.Visible;

            double dblTotal = m_lstFaixas.Sum(f => f.dblDuracao);
            string strPastas = string.Join("  ·  ", m_lstFaixas.Select(f => Path.GetDirectoryName(f.strCaminho)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            m_lblResumoFaixas.Text = string.Format("{0} faixa(s) · {1}   ·   {2}", m_lstFaixas.Count, TFaixaMp3.Formatar(dblTotal), strPastas);
            string strAtual = string.Format("Tags atuais: {0} — {1}",
                MaisComum(m_lstFaixas.Select(f => f.strArtistaAlbum.Length > 0 ? f.strArtistaAlbum : f.strArtista)),
                MaisComum(m_lstFaixas.Select(f => f.strAlbum)));
            if (m_lstIgnorados.Count > 0) strAtual += "   ·   ignorados (não são MP3 nem WMA): " + m_lstIgnorados.Count;
            m_strMcdi = IndiceDoCd();
            if (m_strMcdi != null)
            {
                TTocAudio t = TTocAudio.DeMcdi(m_strMcdi, 0);
                strAtual += string.Format("   ·   índice do CD: {0} faixas", t != null ? t.QtdFaixas : 0);
            }
            m_lblTagsAtuais.Text = strAtual;
        }

        /// <summary>O índice do CD de origem (WM/MCDI) que o WMP gravou nas faixas WMA, ou null.</summary>
        private string m_strMcdi;
        /// <summary>Alguma faixa guarda índice (mesmo de discos diferentes, como um álbum duplo): a conferência no AccurateRip vale.</summary>
        private bool m_blnAlgumIndice;

        /// <summary>
        /// O WM/MCDI das faixas, se todas as que o têm guardam o MESMO índice
        /// (um disco só). Faixas de CDs diferentes não identificam um disco.
        /// </summary>
        private string IndiceDoCd()
        {
            // WMA (WM/MCDI) e MP3 (quadro ID3 MCDI): o WMP grava o mesmo texto nos dois.
            List<string> lst = new List<string>();
            foreach (TFaixaMp3 f in m_lstFaixas)
            {
                string s = TFaixaMp3.IndiceDoCd(f.strCaminho);
                if (!string.IsNullOrEmpty(s)) lst.Add(s);
            }
            m_blnAlgumIndice = lst.Count > 0;
            List<string> lstDistintos = lst.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (lstDistintos.Count == 1) return lstDistintos[0];
            if (lstDistintos.Count > 1) Log("As faixas guardam índices de " + lstDistintos.Count + " CDs diferentes; a busca pelo índice fica de fora.");
            return null;
        }

        // =============================================================== leitura

        private void LerFaixas(List<string> lstArquivos)
        {
            List<TFaixaMp3> lst = new List<TFaixaMp3>();
            foreach (string strArq in lstArquivos.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(strArq)) { m_lstIgnorados.Add(strArq + " (não existe)"); continue; }
                if (!strArq.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) && !TFaixaMp3.EhWma(strArq)) { m_lstIgnorados.Add(strArq); continue; }
                try { lst.Add(TFaixaMp3.Ler(strArq)); }
                catch (Exception ex) { m_lstIgnorados.Add(strArq + " (" + ex.Message + ")"); }
            }
            m_lstFaixas.AddRange(TFaixaMp3.Ordenar(lst));
        }

        private static string MaisComum(IEnumerable<string> seq)
        {
            return seq.Where(s => !string.IsNullOrEmpty(s) && s.Trim().Length > 0)
                      .GroupBy(s => s.Trim(), StringComparer.OrdinalIgnoreCase)
                      .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
        }

        private static bool EhGenerico(string s)
        {
            string t = (s ?? "").Trim().ToLowerInvariant();
            return t == "" || t == "unknown album" || t == "unknown artist" || t == "álbum desconhecido" ||
                   t == "artista desconhecido" || t == "various artists" || t == "vários artistas";
        }


        // =============================================================== identificar

        private void AoIdentificar(object sender, RoutedEventArgs e)
        {
            Identificar(false);
        }

        private void AoIdentificarPeloSom(object sender, RoutedEventArgs e)
        {
            Identificar(true);
        }

        /// <summary>
        /// Pelo link, se houver. Senão: pelo índice do CD (se as faixas o guardam)
        /// ou pelo som (botão "Pelo som", ou quando não há pista nenhuma) — e, com
        /// o nome achado (ou o digitado), também no Apple Music e no Discogs.
        /// </summary>
        private void Identificar(bool blnSom)
        {
            string strArtista = m_txtArtista.Text.Trim(), strAlbum = m_txtAlbum.Text.Trim(), strLink = m_txtLink.Text.Trim();
            string strMcdi = strLink.Length == 0 && !blnSom ? m_strMcdi : null;
            if (strLink.Length == 0 && strArtista.Length == 0 && strAlbum.Length == 0 && strMcdi == null) blnSom = true;   // nenhuma pista: pelo som
            if (blnSom) strLink = "";
            List<string> lstCaminhos = m_lstFaixas.Select(f => f.strCaminho).ToList();
            int intFaixas = m_lstFaixas.Count;
            List<int> lstDuracoes = m_lstFaixas.Select(f => (int)Math.Round(f.dblDuracao)).ToList();
            string strBuscado = strLink.Length > 0 ? strLink : (strArtista + " — " + strAlbum).Trim(' ', '—');
            if (strBuscado.Length == 0) strBuscado = blnSom ? "pelo som" : "pelo índice do CD";

            m_btnIdentificar.IsEnabled = m_btnPeloSom.IsEnabled = false;
            m_btnAplicar.IsEnabled = false;
            Status(strLink.Length > 0 ? "lendo o link no ARCA Central…"
                 : blnSom ? string.Format("ouvindo as faixas pelo AcoustID (uns {0} s; o notebook lê cada uma pela rede)…", Math.Max(5, intFaixas * 2))
                 : strMcdi != null ? "procurando o disco pelo índice do CD (MusicBrainz, GnuDB) e pelo nome (Apple Music, Discogs)…"
                 : "consultando Apple Music e Discogs pelo ARCA Central…", false);
            Log("Pedido: " + strBuscado + " (" + intFaixas + " faixas)" + (strMcdi != null ? " · índice do CD " + strMcdi : "") + (blnSom ? " · pelo som (AcoustID)" : ""));

            Task.Factory.StartNew(delegate
            {
                TResposta rToc = null, r = null; Exception objErro = null;
                try
                {
                    if (strLink.Length > 0) r = ClienteCentral.PorLink(strLink, intFaixas);
                    else
                    {
                        // Pelo índice primeiro: é o disco exato. Sem nome digitado, o nome
                        // que o índice achou vai para a busca no Apple Music e no Discogs.
                        if (blnSom)
                        {
                            try { rToc = ClienteCentral.PorSom(lstCaminhos); } catch (Exception ex) { rToc = new TResposta { strErro = "som: " + ex.Message }; }
                        }
                        else if (strMcdi != null)
                        {
                            try { rToc = ClienteCentral.PorToc(strMcdi); } catch (Exception ex) { rToc = new TResposta { strErro = "índice do CD: " + ex.Message }; }
                        }
                        string a = strArtista, b = strAlbum;
                        if (a.Length == 0 && b.Length == 0 && rToc != null && rToc.lstCandidatos.Count > 0)
                        { a = rToc.lstCandidatos[0].strArtista ?? ""; b = rToc.lstCandidatos[0].strAlbum ?? ""; }
                        if (a.Length > 0 || b.Length > 0) r = ClienteCentral.Buscar(a, b, intFaixas);
                    }
                }
                catch (Exception ex) { objErro = ex; }
                Dispatcher.BeginInvoke((Action)delegate { AoResponder(rToc, r, objErro, lstDuracoes, strBuscado, strLink.Length > 0, blnSom); });
            });
        }

        private void AoResponder(TResposta rToc, TResposta r, Exception objErro, List<int> lstDuracoes, string strBuscado, bool blnLink, bool blnSom)
        {
            m_btnIdentificar.IsEnabled = m_btnPeloSom.IsEnabled = true;
            if (objErro != null) { Status(objErro.Message, true); Log("ERRO: " + objErro.Message); return; }
            if (rToc != null)
            {
                if (rToc.lstLog != null) foreach (string s in rToc.lstLog) Log("  " + s);
                if (!string.IsNullOrEmpty(rToc.strErro)) Log("  " + rToc.strErro);
            }
            if (r != null && r.lstLog != null) foreach (string s in r.lstLog) Log("  " + s);

            List<KeyValuePair<string, TCandidatoIdentificacao>> lstFontes = new List<KeyValuePair<string, TCandidatoIdentificacao>>();
            if (rToc != null)
                foreach (TCandidatoDto d in rToc.lstCandidatos)
                    lstFontes.Add(new KeyValuePair<string, TCandidatoIdentificacao>(d.strFonte + (blnSom ? " · pelo som" : " · pelo índice do CD"), d.ParaCandidato()));
            if (blnLink)
            {
                if (r != null && r.objCandidato != null)
                {
                    TCandidatoIdentificacao c = r.objCandidato.ParaCandidato();
                    lstFontes.Add(new KeyValuePair<string, TCandidatoIdentificacao>(c.strFonte, c));
                }
            }
            else if (r != null)
            {
                lstFontes.Add(new KeyValuePair<string, TCandidatoIdentificacao>("Apple Music", r.objApple != null ? r.objApple.ParaCandidato() : null));
                lstFontes.Add(new KeyValuePair<string, TCandidatoIdentificacao>("Discogs", r.objDiscogs != null ? r.objDiscogs.ParaCandidato() : null));
            }
            if (lstFontes.All(kv => kv.Value == null))
            {
                string strErro = (r != null ? r.strErro : null) ?? (rToc != null ? rToc.strErro : null) ?? "nada encontrado";
                Status(strErro + ". Ajuste o texto ou cole o link exato.", true);
                return;
            }

            JanelaEscolhaFonte objEscolha = new JanelaEscolhaFonte(lstFontes, lstDuracoes, m_lstFaixas.Select(f => TituloProvavel(f)).ToList(), strBuscado);
            objEscolha.Owner = this;
            objEscolha.ShowDialog();
            if (objEscolha.Escolhido == null) { Status("Nenhuma fonte escolhida. Nada foi alterado.", false); return; }
            Propor(objEscolha.Escolhido);
        }

        // =============================================================== proposta

        private void Propor(TCandidatoIdentificacao c)
        {
            m_objEscolhido = c;
            m_painelEscolhido.Visibility = Visibility.Visible;
            m_lblFonte.Text = c.strFonte + (string.IsNullOrEmpty(c.strDetalhe) ? "" : " · " + c.strDetalhe) + " · " + c.lstTitulos.Count + " faixas";
            m_txtArtistaNovo.Text = c.strArtista ?? "";
            m_txtAlbumNovo.Text = c.strAlbum ?? "";
            m_txtAnoNovo.Text = c.strAno ?? "";
            string strGeneros = c.strGenero ?? "";
            m_txtGeneroNovo.Text = strGeneros.Split(',').Select(s => s.Trim()).FirstOrDefault() ?? "";
            m_lblGeneros.Text = strGeneros.Contains(",") ? "Na fonte: " + strGeneros : "";

            bool blnCapaDoDisco = c.arrCapaPlena != null;
            m_strOrigemCapa = blnCapaDoDisco ? "encarte (Catalogar à mão)" : c.strFonte;
            if (c.arrCapa != null && ReferenceEquals(c.arrCapa, m_arrCapaAtual))
                MostrarCapa(c.arrCapa, "a capa que o álbum já tem", false);   // Catalogar à mão sem trocar a capa
            else
                MostrarCapa(c.arrCapa, blnCapaDoDisco ? "do encarte" : null, true);

            Casar(c);
            int intCasadas = m_lstFaixas.Count(f => f.intPosicaoNova >= 0);
            int intAlarmes = m_lstFaixas.Count(f => f.objCorDif == Recurso("Alarme", Brushes.Red));
            m_btnAplicar.IsEnabled = intCasadas > 0;
            Status(string.Format("{0} de {1} faixas casadas com o lançamento ({2} faixas).{3} Confira a grade e os campos à direita; só \"Aplicar às faixas\" grava.",
                intCasadas, m_lstFaixas.Count, c.lstTitulos.Count,
                intAlarmes > 0 ? " ATENÇÃO: " + intAlarmes + " com diferença de duração acima de " + TOLERANCIA_SEGUNDOS + " s." : ""), intAlarmes > 0);
        }

        private void MostrarCapa(byte[] arrCapa, string strNota, bool blnMarcar)
        {
            BitmapImage objCapa = AbrirImagem(arrCapa, 280);
            m_imgCapaNova.Source = objCapa;
            if (arrCapa != null && objCapa != null)
            {
                BitmapImage objTam = AbrirImagem(arrCapa, 0);
                m_lblCapaNova.Text = string.Format("{0} × {1} px · {2:N0} KB{3}", objTam.PixelWidth, objTam.PixelHeight, arrCapa.Length / 1024.0,
                                                   string.IsNullOrEmpty(strNota) ? "" : " · " + strNota);
                m_chkCapa.IsEnabled = true;
                m_chkCapa.IsChecked = blnMarcar;
            }
            else
            {
                m_lblCapaNova.Text = "Sem capa. Digitalize o encarte ou escolha um arquivo.";
                m_chkCapa.IsEnabled = false;
                m_chkCapa.IsChecked = false;
            }
        }

        // =============================================================== capa do disco físico

        private void AoDigitalizarCapa(object sender, RoutedEventArgs e)
        {
            if (m_objEscolhido == null) return;
            string strErro;
            BitmapSource objImagem = ImagensDoEncarte.DoScanner(out strErro);
            if (strErro != null) { Status(strErro, true); Log("scanner: " + strErro); }
            if (objImagem != null) TrocarCapa(objImagem, "digitalizada (G7000)");
        }

        private void AoCapaDeArquivo(object sender, RoutedEventArgs e)
        {
            if (m_objEscolhido == null) return;
            string strErro;
            BitmapSource objImagem = ImagensDoEncarte.DeArquivo(this, "Escolher a capa", out strErro);
            if (strErro != null) Status(strErro, true);
            if (objImagem != null) TrocarCapa(objImagem, "arquivo");
        }

        /// <summary>Corte quadrado; reduzida (1000 px) para as faixas, plena para Artwork\capa.jpg.</summary>
        private void TrocarCapa(BitmapSource objImagem, string strOrigem)
        {
            BitmapSource objCortada = ImagensDoEncarte.Cortar(this, objImagem, "Cortar a capa", true);
            if (objCortada == null) return;
            byte[] arrReduzida = ImagensDoEncarte.CodificarReduzida(objCortada);
            byte[] arrPlena = ImagensDoEncarte.CodificarPlena(objCortada);
            if (arrReduzida == null || arrPlena == null) { Status("Não consegui preparar a capa.", true); return; }
            m_objEscolhido.arrCapa = arrReduzida;
            m_objEscolhido.arrCapaPlena = arrPlena;
            m_strOrigemCapa = strOrigem;
            MostrarCapa(arrReduzida, strOrigem, true);
            Log("Capa trocada: " + strOrigem + string.Format(", {0} × {1} px", objCortada.PixelWidth, objCortada.PixelHeight));
            Status("Capa trocada. A original vai também para Artwork\\capa.jpg, sem redução.", false);
        }

        // =============================================================== catalogar à mão

        /// <summary>
        /// A JanelaEditorFaixas: número e duração vêm dos
        /// arquivos e não se editam; títulos partem do que o arquivo já diz.
        /// O resultado é um candidato como os do Discogs e da Apple.
        /// </summary>
        private void AoCatalogar(object sender, RoutedEventArgs e)
        {
            List<int> lstDuracoes = m_lstFaixas.Select(f => f.dblDuracao > 0 ? (int)Math.Round(f.dblDuracao) : -1).ToList();
            List<string> lstTitulos = m_lstFaixas.Select(f => TituloProvavel(f)).ToList();
            JanelaEditorFaixas objJanela = new JanelaEditorFaixas(lstDuracoes, m_txtArtista.Text.Trim(), m_txtAlbum.Text.Trim(),
                                                                  lstTitulos, null, m_arrCapaAtual);
            objJanela.Owner = this;
            objJanela.ShowDialog();
            if (objJanela.Resultado == null) { Status("Catalogação à mão cancelada. Nada foi alterado.", false); return; }
            TCandidatoIdentificacao c = objJanela.Resultado;
            Log(string.Format("Catalogado à mão: {0} — {1}, {2} faixas{3}.", c.strArtista, c.strAlbum, c.lstTitulos.Count,
                c.lstEncarte != null && c.lstEncarte.Count > 0 ? ", " + c.lstEncarte.Count + " página(s) do encarte" : ""));
            Propor(c);
        }

        /// <summary>O título da tag; se for genérico ("Faixa 3", "Track 03"), o nome do arquivo sem o número.</summary>
        private static string TituloProvavel(TFaixaMp3 f)
        {
            string s = (f.strTitulo ?? "").Trim();
            if (s.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(s, @"^(faixa( desconhecida)?|track|unknown track)\s*\d*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                s = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(f.strCaminho), @"^\s*(\d{1,3}[\s._-]+)+", "").Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^(faixa( desconhecida)?|track)\s*\d*(\s+\d+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) s = "";
            }
            return s;
        }

        /// <summary>
        /// Casa as faixas do disco com as do lançamento. Mesma quantidade: pela
        /// ordem (como a JanelaEscolhaFonte compara). Quantidades diferentes
        /// (álbum incompleto, comum em MP3): pelo menor custo, que soma a
        /// diferença de duração e a distância entre os títulos.
        /// </summary>
        private void Casar(TCandidatoIdentificacao c)
        {
            int n = m_lstFaixas.Count, m = c.lstTitulos.Count;
            int[] vetPar = Enumerable.Repeat(-1, n).ToArray();
            if (n == m) for (int i = 0; i < n; i++) vetPar[i] = i;
            else
            {
                List<Tuple<double, int, int>> lstCustos = new List<Tuple<double, int, int>>();
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < m; j++)
                    {
                        int intDurFonte = j < c.lstDuracoes.Count ? c.lstDuracoes[j] : -1;
                        double dblDur = intDurFonte > 0 ? Math.Abs(intDurFonte - Math.Round(m_lstFaixas[i].dblDuracao)) : 30;
                        double dblSim = DiscogsClient.CalcularSimilaridadeTexto(m_lstFaixas[i].strTitulo, c.lstTitulos[j]);
                        lstCustos.Add(Tuple.Create(Math.Min(dblDur, 120) + (1 - dblSim) * 60, i, j));
                    }
                bool[] vetUsadaFonte = new bool[m];
                foreach (Tuple<double, int, int> t in lstCustos.OrderBy(x => x.Item1))
                {
                    if (t.Item1 > 90) break;
                    if (vetPar[t.Item2] >= 0 || vetUsadaFonte[t.Item3]) continue;
                    vetPar[t.Item2] = t.Item3;
                    vetUsadaFonte[t.Item3] = true;
                }
                Log(string.Format("Quantidades diferentes ({0} no disco, {1} no lançamento): casadas por duração e título.", n, m));
            }

            Brush objAlarme = Recurso("Alarme", Brushes.Red), objOk = Recurso("TextoFraco", Brushes.Gray);
            for (int i = 0; i < n; i++)
            {
                TFaixaMp3 f = m_lstFaixas[i];
                int j = vetPar[i];
                f.intPosicaoNova = j;
                f.strResultado = "";
                if (j < 0)
                {
                    f.strTituloNovo = ""; f.strArtistaNovo = ""; f.strCompositorNovo = "";
                    f.strDif = "sem par"; f.objCorDif = objAlarme; f.blnAplicar = false;
                    continue;
                }
                f.strTituloNovo = c.lstTitulos[j];
                string strArtFaixa = j < c.lstArtistasFaixas.Count ? c.lstArtistasFaixas[j] : "";
                f.strArtistaNovo = string.Equals((strArtFaixa ?? "").Trim(), (c.strArtista ?? "").Trim(), StringComparison.OrdinalIgnoreCase) ? "" : strArtFaixa;
                f.strCompositorNovo = c.lstCompositores != null && j < c.lstCompositores.Count ? c.lstCompositores[j] : "";
                int intDur = j < c.lstDuracoes.Count ? c.lstDuracoes[j] : -1;
                if (intDur > 0)
                {
                    int intDif = intDur - (int)Math.Round(f.dblDuracao);
                    f.strDif = (intDif > 0 ? "+" : intDif < 0 ? "−" : "") + Math.Abs(intDif) + " s";
                    f.objCorDif = Math.Abs(intDif) > TOLERANCIA_SEGUNDOS ? objAlarme : objOk;
                }
                else { f.strDif = "?"; f.objCorDif = objOk; }
                f.blnAplicar = true;
            }
        }

        // =============================================================== aplicar

        private void AoAplicar(object sender, RoutedEventArgs e)
        {
            if (m_objEscolhido == null) return;
            List<TFaixaMp3> lstMarcadas = m_lstFaixas.Where(f => f.blnAplicar && f.intPosicaoNova >= 0).ToList();
            if (lstMarcadas.Count == 0) { Status("Nenhuma faixa marcada (✓) para gravar.", true); return; }
            string strArtista = m_txtArtistaNovo.Text.Trim(), strAlbum = m_txtAlbumNovo.Text.Trim();
            if (strArtista.Length == 0 || strAlbum.Length == 0) { Status("Artista do álbum e Álbum não podem ficar vazios.", true); return; }
            string strAno = m_txtAnoNovo.Text.Trim();
            if (strAno.Length > 0 && (strAno.Length != 4 || !strAno.All(char.IsDigit))) { Status("O ano tem de ter quatro dígitos (ou ficar vazio).", true); return; }

            bool blnCapa = m_chkCapa.IsChecked == true && m_objEscolhido.arrCapa != null;
            int intPaginas = m_objEscolhido.lstEncarte == null ? 0 : m_objEscolhido.lstEncarte.Count;
            string strArtwork = (blnCapa && m_objEscolhido.arrCapaPlena != null ? "Artwork\\capa.jpg" : "") +
                                (intPaginas > 0 ? (blnCapa && m_objEscolhido.arrCapaPlena != null ? " e " : "Artwork\\: ") + intPaginas + " página(s) do encarte" : "");
            string strPergunta = string.Format(
                "Gravar em {0} faixa(s):\n\n  {1} — {2}{3}{4}\n  títulos, intérpretes e números de faixa da grade\n  {5}" +
                (strArtwork.Length > 0 ? "\n  " + strArtwork : "") + "\n\n" +
                "O áudio não é tocado: cada faixa é gravada numa cópia, conferida (SHA-256 do áudio) e só então trocada.\n" +
                "O que havia antes fica registrado no ledger do acervo.",
                lstMarcadas.Count, strArtista, strAlbum,
                strAno.Length > 0 ? " (" + strAno + ")" : "",
                m_txtGeneroNovo.Text.Trim().Length > 0 ? " · " + m_txtGeneroNovo.Text.Trim() : "",
                blnCapa ? "capa embutida + Folder.jpg e AlbumArtSmall.jpg" : "sem mudar a capa");
            if (MessageBox.Show(this, strPergunta, "Aplicar às faixas", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            TDadosAlbum objDados = new TDadosAlbum
            {
                strArtistaAlbum = strArtista,
                strAlbum = strAlbum,
                strAno = strAno,
                strGenero = m_txtGeneroNovo.Text.Trim(),
                intTotalFaixas = m_objEscolhido.lstTitulos.Count,
                strFonte = m_objEscolhido.strFonte + (string.IsNullOrEmpty(m_objEscolhido.strDetalhe) ? "" : " · " + m_objEscolhido.strDetalhe),
                arrCapa = blnCapa ? m_objEscolhido.arrCapa : null,
                arrCapaPlena = blnCapa ? m_objEscolhido.arrCapaPlena : null,
                lstEncarte = intPaginas > 0 ? m_objEscolhido.lstEncarte : null,
                strOrigemCapa = m_strOrigemCapa,
                strEvento = strEventoAoAplicar
            };

            m_btnAplicar.IsEnabled = false;
            m_btnIdentificar.IsEnabled = false;
            m_btnCatalogar.IsEnabled = false;
            m_btnFechar.IsEnabled = false;
            m_blnGravando = true;
            Status("gravando…", false);
            Action<string> actLog = delegate(string s) { Dispatcher.BeginInvoke((Action)delegate { Log(s); }); Programa.Registrar(s); };
            Action<int, int> actProgresso = delegate(int i, int n)
            {
                Dispatcher.BeginInvoke((Action)delegate { Status(string.Format("gravando faixa {0} de {1}… (não feche a janela)", i, n), false); });
            };
            Task.Factory.StartNew(delegate
            {
                int intOk = 0; Exception objErro = null;
                string strDestino = strRaizParaMover == null ? null : SeparadorDesconhecido.PastaDoDisco(strRaizParaMover, strArtista, blnAnoNaPasta ? strAno : null, strAlbum);
                objDados.blnRenomearAoMover = strDestino != null;
                try { intOk = GravadorMp3.Aplicar(m_lstFaixas, objDados, actLog, actProgresso, strDestino, true); }
                catch (Exception ex) { objErro = ex; }
                Programa.Registrar(objErro != null ? "gravação interrompida: " + objErro : "gravação concluída: " + intOk + " faixa(s)");
                Dispatcher.BeginInvoke((Action)delegate
                {
                    m_blnGravando = false;
                    m_btnFechar.IsEnabled = true;
                    m_btnIdentificar.IsEnabled = true;
                    m_btnCatalogar.IsEnabled = true;
                    if (objErro != null) { Status("Falhou: " + objErro.Message, true); Log("ERRO: " + objErro); return; }
                    bool blnTudo = intOk == lstMarcadas.Count;
                    Status(string.Format("{0} de {1} faixa(s) gravada(s), áudio conferido. O WMP mostra as mudanças sozinho em alguns segundos.{2}",
                        intOk, lstMarcadas.Count, blnTudo ? "" : " Veja na coluna Resultado as que falharam (o original delas não mudou)."), !blnTudo);
                });
            });
        }

        // =============================================================== utilidades

        private void Status(string strTexto, bool blnAlarme)
        {
            m_lblStatus.Text = strTexto;
            m_lblStatus.Foreground = blnAlarme ? Recurso("Alarme", Brushes.Red) : Recurso("Texto", Brushes.Black);
        }

        private void Log(string strTexto)
        {
            m_txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + strTexto + Environment.NewLine);
            m_txtLog.ScrollToEnd();
        }

        private Brush Recurso(string strChave, Brush objPadrao)
        {
            return (TryFindResource(strChave) as Brush) ?? objPadrao;
        }

        /// <summary>Decodifica já reduzida (intLargura &gt; 0) para não gastar memória à toa.</summary>
        private static BitmapImage AbrirImagem(byte[] arr, int intLargura)
        {
            if (arr == null || arr.Length == 0) return null;
            try
            {
                BitmapImage bi = new BitmapImage();
                bi.BeginInit();
                bi.StreamSource = new MemoryStream(arr);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                if (intLargura > 0) bi.DecodePixelWidth = intLargura;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }
    }
}
