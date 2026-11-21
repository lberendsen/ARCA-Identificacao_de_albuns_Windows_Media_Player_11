using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ARCA.Central;

namespace ARCA.Identificar
{
    /// <summary>
    /// Separar o Álbum Desconhecido: cada faixa solta é procurada no Apple
    /// Music (pelo ARCA Central) pelo título que a tag ou o nome do arquivo dá,
    /// conferida pela duração e pelo artista, e as que conferem são agrupadas
    /// nos discos de onde vieram. "Aplicar" grava disco a disco — tags, capa,
    /// número — e move cada um para MP3\Artista\Ano - Álbum.
    ///
    /// O que a busca por título não resolve (clássico com nome genérico,
    /// séries de CDs) sai pelo filtro de indício e pela janela de identificar
    /// um álbum, a de sempre.
    /// </summary>
    public partial class JanelaSepararDesconhecido : Window
    {
        private sealed class TFiltro
        {
            public string strNome; public Func<TFaixaSolta, bool> fnPassa;
            public override string ToString() { return strNome; }
        }

        private readonly List<TFaixaSolta> m_lstLinhas = new List<TFaixaSolta>();
        private readonly List<string> m_lstIgnorados = new List<string>();
        private readonly string m_strRaiz;
        private ICollectionView m_objVista;
        private volatile bool m_blnParar;
        private bool m_blnOcupado;

        public JanelaSepararDesconhecido(List<string> lstArquivos)
        {
            InitializeComponent();
            foreach (string s in lstArquivos.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(s) || !(s.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || TFaixaMp3.EhWma(s))) { m_lstIgnorados.Add(s); continue; }
                try
                {
                    TFaixaSolta l = new TFaixaSolta { objFaixa = TFaixaMp3.Ler(s) };
                    CasadorFaixasSoltas.Preparar(l);
                    m_lstLinhas.Add(l);
                }
                catch (Exception ex) { m_lstIgnorados.Add(s + " (" + ex.Message + ")"); }
            }
            m_lstLinhas.Sort((a, b) => string.Compare(a.strArquivo, b.strArquivo, StringComparison.OrdinalIgnoreCase));
            m_strRaiz = m_lstLinhas.Count > 0 ? SeparadorDesconhecido.RaizDoAcervo(m_lstLinhas[0].objFaixa.strCaminho) : "";

            int intSemTitulo = m_lstLinhas.Count(l => string.IsNullOrEmpty(l.strTituloBusca));
            m_lblResumoEntrada.Text = string.Format("{0} faixa(s) sem álbum; {1} com título para procurar, {2} sem nada no nome. " +
                "Cada uma é procurada pelo título e conferida pela duração e pelo artista.", m_lstLinhas.Count, m_lstLinhas.Count - intSemTitulo, intSemTitulo);
            m_lblExemploPasta.Text = Path.Combine(m_strRaiz, @"Artista\Ano - Álbum\NN Título.mp3");

            m_grade.ItemsSource = m_lstLinhas;
            m_objVista = CollectionViewSource.GetDefaultView(m_lstLinhas);
            MontarFiltros();
            foreach (TFaixaSolta l in m_lstLinhas)
                l.PropertyChanged += delegate(object s, PropertyChangedEventArgs ev)
                {
                    if (ev.PropertyName != "blnAplicar" && ev.PropertyName != "objEscolhida") return;
                    if (Dispatcher.CheckAccess()) AtualizarDiscos(); else Dispatcher.BeginInvoke((Action)AtualizarDiscos);
                };
            foreach (string s in m_lstIgnorados) Log("ignorado: " + s);
            Status(m_lstLinhas.Count == 0 ? "Nenhuma faixa MP3 ou WMA recebida."
                : "Clique em \"Procurar no Apple Music\". Nada muda nos arquivos até \"Aplicar\".", m_lstLinhas.Count == 0);
            m_btnBuscar.IsEnabled = m_lstLinhas.Count > 0;
            Closing += delegate(object s, CancelEventArgs e)
            {
                if (!m_blnOcupado) return;
                e.Cancel = true;
                MessageBox.Show(this, "Espere terminar (ou clique em Parar, na busca).", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            };
        }

        private void AoFechar(object sender, RoutedEventArgs e) { Close(); }

        // ============================================================ filtro

        /// <summary>Todas, com/sem proposta, cada indício que junta 2+ faixas, e cada disco proposto.</summary>
        private void MontarFiltros()
        {
            TFiltro objAtual = m_cmbFiltro.SelectedItem as TFiltro;
            List<TFiltro> lst = new List<TFiltro>
            {
                new TFiltro { strNome = "Todas (" + m_lstLinhas.Count + ")", fnPassa = l => true },
                new TFiltro { strNome = "Com disco proposto (" + m_lstLinhas.Count(l => l.objOpcao != null) + ")", fnPassa = l => l.objOpcao != null },
                new TFiltro { strNome = "Sem disco proposto (" + m_lstLinhas.Count(l => l.objOpcao == null) + ")", fnPassa = l => l.objOpcao == null },
                new TFiltro { strNome = "Ainda não gravadas (" + m_lstLinhas.Count(l => !Feita(l)) + ")", fnPassa = l => !Feita(l) },
            };
            foreach (var g in m_lstLinhas.Where(l => l.strIndicio.Length > 0).GroupBy(l => l.strIndicio, StringComparer.OrdinalIgnoreCase)
                                         .Where(g => g.Count() >= 2).OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
            {
                string k = g.Key;
                lst.Add(new TFiltro { strNome = "Indício — " + k + " (" + g.Count() + ")", fnPassa = l => string.Equals(l.strIndicio, k, StringComparison.OrdinalIgnoreCase) });
            }
            foreach (var g in m_lstLinhas.Where(l => l.objOpcao != null).GroupBy(l => l.objOpcao.objApple.lngAlbumId)
                                         .OrderByDescending(g => g.Count()).ThenBy(g => g.First().strDisco))
            {
                long id = g.Key;
                lst.Add(new TFiltro { strNome = "Disco — " + g.First().strDisco + " (" + g.Count() + ")", fnPassa = l => l.objOpcao != null && l.objOpcao.objApple.lngAlbumId == id });
            }
            m_cmbFiltro.ItemsSource = lst;
            m_cmbFiltro.SelectedItem = (objAtual == null ? null : lst.FirstOrDefault(f => f.strNome.Split('(')[0] == objAtual.strNome.Split('(')[0])) ?? lst[0];
        }

        private void AoFiltrar(object sender, SelectionChangedEventArgs e)
        {
            TFiltro f = m_cmbFiltro.SelectedItem as TFiltro;
            if (m_objVista == null || f == null) return;
            m_grade.CommitEdit(DataGridEditingUnit.Row, true);
            m_objVista.Filter = o => f.fnPassa((TFaixaSolta)o);
        }

        private static bool Feita(TFaixaSolta l) { return (l.strResultado ?? "").StartsWith("gravada") || (l.strResultado ?? "").StartsWith("identificada"); }

        private List<TFaixaSolta> Mostradas() { return m_objVista.Cast<TFaixaSolta>().ToList(); }

        // ============================================================ busca

        private void AoBuscar(object sender, RoutedEventArgs e)
        {
            Buscar(m_lstLinhas.Where(l => !l.blnBuscada && !Feita(l) && !string.IsNullOrEmpty(l.strTituloBusca)).ToList());
        }

        private void AoBuscarDeNovo(object sender, RoutedEventArgs e)
        {
            m_grade.CommitEdit(DataGridEditingUnit.Row, true);
            List<TFaixaSolta> lst = m_grade.SelectedItems.OfType<TFaixaSolta>().Where(l => !Feita(l) && !string.IsNullOrEmpty(l.strTituloBusca)).ToList();
            if (lst.Count == 0) { Status("Selecione na grade as faixas a procurar de novo (com título de busca).", true); return; }
            foreach (TFaixaSolta l in lst) l.blnBuscada = false;
            Buscar(lst);
        }

        private void AoParar(object sender, RoutedEventArgs e)
        {
            m_blnParar = true;
            m_btnParar.IsEnabled = false;
            Status("parando depois da faixa atual…", false);
        }

        private void Buscar(List<TFaixaSolta> lst)
        {
            if (lst.Count == 0) { Status("Não há faixas a procurar.", false); return; }
            m_grade.CommitEdit(DataGridEditingUnit.Row, true);
            m_blnParar = false;
            m_blnOcupado = true;
            m_btnBuscar.IsEnabled = m_btnBuscarDeNovo.IsEnabled = m_btnAplicar.IsEnabled = m_btnComoAlbum.IsEnabled = false;
            m_btnParar.IsEnabled = true;
            Log("Busca: " + lst.Count + " faixa(s).");

            Task.Factory.StartNew(delegate
            {
                int intFeitas = 0, intCache = 0, intErrosSeguidos = 0;
                string strErroFinal = null;
                foreach (TFaixaSolta l in lst)
                {
                    if (m_blnParar) break;
                    string strTitulo = null, strArtista = null;
                    Dispatcher.Invoke((Action)delegate { strTitulo = l.strTituloBusca; strArtista = l.strArtistaBusca; });
                    TBuscaFaixaDto b = null;
                    try { b = ClienteCentral.Faixa(strArtista, strTitulo); intErrosSeguidos = 0; }
                    catch (Exception ex)
                    {
                        if (++intErrosSeguidos >= 3) { strErroFinal = ex.Message; break; }
                        continue;
                    }
                    if (b.blnDoCache) intCache++;
                    intFeitas++;
                    int n = intFeitas, c = intCache;
                    Dispatcher.Invoke((Action)delegate
                    {
                        List<object> lstOp = CasadorFaixasSoltas.Opcoes(l, b).Cast<object>().ToList();
                        lstOp.Add(TFaixaSolta.NAO_MEXER);
                        l.lstOpcoes = lstOp;
                        l.blnBuscada = true;
                        CasadorFaixasSoltas.Consolidar(new List<TFaixaSolta> { l }, m_chkSoAlta.IsChecked == true);
                        if (!string.IsNullOrEmpty(b.strErro)) Log("  " + l.strArquivo + ": " + b.strErro);
                        Status(string.Format("procurando: {0} de {1} ({2} do cache)… {3}", n, lst.Count, c, strTitulo), false);
                    });
                }
                Dispatcher.BeginInvoke((Action)delegate
                {
                    // Com tudo buscado, a escolha final puxa as faixas para os discos em comum.
                    CasadorFaixasSoltas.Consolidar(m_lstLinhas, m_chkSoAlta.IsChecked == true);
                    m_blnOcupado = false;
                    m_btnBuscar.IsEnabled = m_btnBuscarDeNovo.IsEnabled = m_btnComoAlbum.IsEnabled = true;
                    m_btnParar.IsEnabled = false;
                    m_grade.Items.Refresh();
                    MontarFiltros();
                    AtualizarDiscos();
                    int intCom = m_lstLinhas.Count(l => l.objOpcao != null), intAlta = m_lstLinhas.Count(l => l.strConfianca == "alta");
                    Log(string.Format("Busca: {0} procurada(s), {1} do cache. Com disco: {2} de {3} (alta {4}, média {5}, baixa {6}).",
                        intFeitas, intCache, intCom, m_lstLinhas.Count, intAlta,
                        m_lstLinhas.Count(l => l.strConfianca == "média"), m_lstLinhas.Count(l => l.strConfianca == "baixa")));
                    if (strErroFinal != null) { Status("A busca parou: " + strErroFinal, true); Log("ERRO: " + strErroFinal); return; }
                    m_btnAplicar.IsEnabled = intCom > 0;
                    Status(string.Format("{0} de {1} faixas com disco proposto ({2} de confiança alta){3}. Confira — sobretudo média e baixa — e clique em Aplicar.",
                        intCom, m_lstLinhas.Count, intAlta, m_blnParar ? ". Busca interrompida: \"Procurar\" continua de onde parou" : ""), false);
                });
            });
        }

        private void AoMarcarSoAlta(object sender, RoutedEventArgs e)
        {
            foreach (TFaixaSolta l in m_lstLinhas)
                if (!Feita(l)) l.blnAplicar = l.objOpcao != null && (m_chkSoAlta.IsChecked != true || l.strConfianca == "alta");
            AtualizarDiscos();
        }

        private void AtualizarDiscos()
        {
            var grupos = m_lstLinhas.Where(l => l.blnAplicar && l.objOpcao != null).GroupBy(l => l.objOpcao.objApple.lngAlbumId)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.First().strDisco).ToList();
            if (grupos.Count == 0) { m_lblDiscos.Text = m_lstLinhas.Any(l => l.blnBuscada) ? "Nenhuma faixa marcada." : "(depois da busca)"; return; }
            IEnumerable<string> lstTexto = grupos.Take(40).Select(g => string.Format("{0} — {1} de {2}", g.First().strDisco, g.Count(), g.First().objOpcao.objApple.intTotalFaixas));
            m_lblDiscos.Text = string.Join("\n", lstTexto.ToArray()) + (grupos.Count > 40 ? "\n… e mais " + (grupos.Count - 40) : "") +
                               string.Format("\n\n{0} disco(s), {1} faixa(s) marcadas.", grupos.Count, grupos.Sum(g => g.Count()));
        }

        // ============================================================ aplicar

        private void AoAplicar(object sender, RoutedEventArgs e)
        {
            m_grade.CommitEdit(DataGridEditingUnit.Row, true);
            List<List<TFaixaSolta>> lstDiscos = m_lstLinhas.Where(l => l.blnAplicar && l.objOpcao != null && !Feita(l))
                .GroupBy(l => l.objOpcao.objApple.lngAlbumId).Select(g => g.ToList()).OrderByDescending(g => g.Count).ToList();
            if (lstDiscos.Count == 0) { Status("Nenhuma faixa marcada.", true); return; }
            bool blnCapas = m_chkCapas.IsChecked == true, blnMover = m_chkMover.IsChecked == true;
            if (MessageBox.Show(this, string.Format(
                    "Gravar {0} faixa(s) em {1} disco(s):\n\n  artista, álbum, ano, número e título de cada faixa, como no Apple Music\n  {2}\n  {3}\n\n" +
                    "O áudio não é tocado: cada faixa é gravada numa cópia, conferida (SHA-256 do áudio) e só então trocada.\n" +
                    "Tudo fica registrado no ledger do acervo.",
                    lstDiscos.Sum(d => d.Count), lstDiscos.Count,
                    blnCapas ? "capa de cada disco" : "sem mexer nas capas",
                    blnMover ? "cada disco movido para " + Path.Combine(m_strRaiz, @"Artista\Ano - Álbum") + ", faixas renomeadas \"NN Título\"" : "as faixas ficam onde estão"),
                    Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            m_blnOcupado = true;
            m_btnAplicar.IsEnabled = m_btnBuscar.IsEnabled = m_btnBuscarDeNovo.IsEnabled = m_btnComoAlbum.IsEnabled = m_btnFechar.IsEnabled = false;
            Action<string> actLog = delegate(string s) { Dispatcher.BeginInvoke((Action)delegate { Log(s); }); Programa.Registrar(s); };
            Task.Factory.StartNew(delegate
            {
                int intOk = 0, intTotal = lstDiscos.Sum(d => d.Count), i = 0;
                foreach (List<TFaixaSolta> lst in lstDiscos)
                {
                    int n = ++i;
                    string strNome = lst[0].strDisco;
                    Dispatcher.BeginInvoke((Action)delegate { Status(string.Format("disco {0} de {1}: {2}… (não feche a janela)", n, lstDiscos.Count, strNome), false); });
                    try { intOk += SeparadorDesconhecido.GravarDisco(lst, m_strRaiz, blnCapas, blnMover, actLog); }
                    catch (Exception ex)
                    {
                        actLog("FALHA no disco " + strNome + ": " + ex.Message + " (as faixas dele não foram alteradas)");
                        foreach (TFaixaSolta l in lst) l.strResultado = "não gravada: " + ex.Message;
                    }
                }
                Programa.Registrar("separação: " + intOk + " de " + intTotal + " faixa(s) gravada(s)");
                LimparPastasEsvaziadas(actLog);
                Dispatcher.BeginInvoke((Action)delegate
                {
                    m_blnOcupado = false;
                    m_btnFechar.IsEnabled = m_btnBuscar.IsEnabled = m_btnBuscarDeNovo.IsEnabled = m_btnComoAlbum.IsEnabled = true;
                    foreach (TFaixaSolta l in m_lstLinhas) if (Feita(l)) l.blnAplicar = false;
                    m_btnAplicar.IsEnabled = m_lstLinhas.Any(l => l.objOpcao != null && !Feita(l));
                    m_grade.Items.Refresh();
                    MontarFiltros();
                    AtualizarDiscos();
                    Status(string.Format("{0} de {1} faixa(s) gravada(s), áudio conferido. O WMP mostra as mudanças sozinho.{2}", intOk, intTotal,
                        intOk == intTotal ? "" : " Veja a coluna Resultado e o registro abaixo."), intOk != intTotal);
                });
            });
        }

        // ============================================================ como um álbum

        /// <summary>As faixas mostradas pelo filtro vão para a janela de identificar um álbum; se ela gravar, move para a pasta do disco.</summary>
        private void AoIdentificarComoAlbum(object sender, RoutedEventArgs e)
        {
            List<TFaixaSolta> lst = Mostradas().Where(l => !Feita(l)).ToList();
            if (lst.Count == 0) { Status("Nenhuma faixa mostrada (ou todas já gravadas). Escolha um indício em \"Mostrar\".", true); return; }
            if (lst.Count > 60 && MessageBox.Show(this, lst.Count + " faixas como um álbum só? Escolha antes um indício em \"Mostrar\".",
                    Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            JanelaIdentificar j = new JanelaIdentificar(lst.Select(l => l.objFaixa.strCaminho).ToList());
            j.strRaizParaMover = m_chkMover.IsChecked == true ? m_strRaiz : null;
            // sem artista na tag: o da busca (que pode ter vindo do nome do arquivo ou da pasta de cima)
            if (j.m_txtArtista.Text.Trim().Length == 0)
                j.m_txtArtista.Text = lst.Select(l => l.strArtistaBusca).Where(s => !string.IsNullOrEmpty(s))
                                         .GroupBy(s => s, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
            j.Owner = this;
            j.ShowDialog();
            LimparPastasEsvaziadas(delegate(string s) { Log(s); Programa.Registrar(s); });
            int n = 0;
            foreach (TFaixaSolta l in lst)
            {
                string strCaminho = l.objFaixa.strCaminho;
                bool blnMovida = !File.Exists(strCaminho);
                bool blnComAlbum = false;
                if (!blnMovida) { try { blnComAlbum = TFaixaMp3.Ler(strCaminho).strAlbum.Trim().Length > 0; } catch { } }
                if (blnMovida || blnComAlbum)
                {
                    l.strResultado = blnMovida ? "identificada como álbum e movida" : "identificada como álbum";
                    l.blnAplicar = false;
                    n++;
                }
            }
            if (n > 0) { Log(n + " faixa(s) identificada(s) pela janela de álbum."); MontarFiltros(); }
            Status(n > 0 ? n + " faixa(s) identificada(s) como álbum." : "Nada foi gravado pela janela de álbum.", false);
        }

        /// <summary>
        /// A pasta de origem que ficou sem nenhuma faixa (CD ripado sem informação,
        /// "Álbum Desconhecido (data)", todo identificado e movido): sai, com as
        /// miniaturas; as AlbumArt_* do WMP vão como backup oculto para onde foi a maioria das faixas.
        /// </summary>
        private void LimparPastasEsvaziadas(Action<string> actLog)
        {
            foreach (var g in m_lstLinhas.GroupBy(l => l.strPastaOrigem, StringComparer.OrdinalIgnoreCase))
            {
                string strPasta = g.Key;
                try
                {
                    if (!Directory.Exists(strPasta)) continue;
                    if (Directory.GetFiles(strPasta).Any(s => s.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || TFaixaMp3.EhWma(s))) continue;
                    string strDestino = g.Select(l => Path.GetDirectoryName(l.objFaixa.strCaminho))
                                         .Where(d => !string.Equals(d, strPasta, StringComparison.OrdinalIgnoreCase))
                                         .GroupBy(d => d, StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault();
                    if (strDestino == null) continue;
                    GravadorMp3.LimparPastaEsvaziada(strPasta, strDestino, actLog);
                }
                catch (Exception ex) { actLog("a pasta " + strPasta + " ficou como estava: " + ex.Message); }
            }
        }

        // ============================================================ utilidades

        private void Status(string s, bool blnAlarme)
        {
            m_lblStatus.Text = s;
            m_lblStatus.Foreground = (TryFindResource(blnAlarme ? "Alarme" : "Texto") as Brush) ?? (blnAlarme ? Brushes.Red : Brushes.Black);
        }

        private void Log(string s)
        {
            m_txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + Environment.NewLine);
            m_txtLog.ScrollToEnd();
        }
    }
}
