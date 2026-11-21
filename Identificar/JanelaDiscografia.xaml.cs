using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ARCA.Central;
using ARCA.Modelo;

namespace ARCA.Identificar
{
    /// <summary>
    /// Organizar pela discografia: vários "álbuns" confusos de um artista
    /// (álbum que é só o ano, pastas misturadas) e, pelo título de cada faixa,
    /// o ano e a duração, o disco certo de cada uma — com capa, nome e número.
    ///
    /// A discografia vem do Discogs pelo ARCA Central (a primeira leitura de um
    /// artista leva minutos; depois fica guardada no notebook). O casamento é
    /// feito aqui, e cada faixa mostra o disco proposto, as alternativas e a
    /// confiança. Nada é gravado até "Aplicar".
    ///
    /// O desenho está no JanelaDiscografia.xaml; aqui só o comportamento.
    /// </summary>
    public partial class JanelaDiscografia : Window
    {
        private readonly List<TLinhaDisco> m_lstLinhas = new List<TLinhaDisco>();
        private readonly List<string> m_lstIgnorados = new List<string>();
        private TDiscografiaDto m_objDiscografia;
        private bool m_blnGravando;

        public JanelaDiscografia(IEnumerable<string> lstArquivos)
        {
            InitializeComponent();

            foreach (string s in lstArquivos.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(s) || !(s.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || TFaixaMp3.EhWma(s))) { m_lstIgnorados.Add(s); continue; }
                try
                {
                    TFaixaMp3 f = TFaixaMp3.Ler(s);
                    string strPasta = Path.GetFileName(Path.GetDirectoryName(s));
                    m_lstLinhas.Add(new TLinhaDisco
                    {
                        objFaixa = f,
                        objEntrada = new TFaixaEntrada
                        {
                            strTitulo = string.IsNullOrEmpty(f.strTitulo) ? Path.GetFileNameWithoutExtension(s) : f.strTitulo,
                            intAno = CasadorDiscografia.AnoSugerido(f.uintAno, f.strAlbum, strPasta),
                            intDuracao = (int)Math.Round(f.dblDuracao),
                            strGrupo = Path.GetDirectoryName(s).ToLowerInvariant() + "|" + (f.strAlbum ?? "").ToLowerInvariant()
                        },
                        lstOpcoes = new List<object> { TLinhaDisco.NAO_MEXER }
                    });
                }
                catch (Exception ex) { m_lstIgnorados.Add(s + " (" + ex.Message + ")"); }
            }

            int intPastas = m_lstLinhas.Select(l => Path.GetDirectoryName(l.objFaixa.strCaminho)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            m_lblResumoEntrada.Text = string.Format("{0} faixa(s) em {1} pasta(s). Pelo título de cada faixa, o ano e a duração, o disco de onde ela veio.", m_lstLinhas.Count, intPastas);
            m_txtArtista.Text = m_lstLinhas.Select(l => l.objFaixa.strArtistaAlbum.Length > 0 ? l.objFaixa.strArtistaAlbum : l.objFaixa.strArtista)
                .Where(s => !string.IsNullOrEmpty(s)).GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
            m_grade.ItemsSource = m_lstLinhas;

            foreach (string s in m_lstIgnorados) Log("ignorado: " + s);
            foreach (TLinhaDisco l in m_lstLinhas)
                l.PropertyChanged += delegate(object s, PropertyChangedEventArgs ev)
                {
                    if (ev.PropertyName != "blnAplicar" && ev.PropertyName != "objEscolhida") return;
                    if (Dispatcher.CheckAccess()) AtualizarResumo(); else Dispatcher.BeginInvoke((Action)AtualizarResumo);
                };
            Status(m_lstLinhas.Count == 0 ? "Nenhuma faixa MP3 ou WMA recebida."
                : "Confira o artista e clique em \"Ler discografia\". Nada muda nos arquivos até \"Aplicar\".", m_lstLinhas.Count == 0);
            m_btnLer.IsEnabled = m_lstLinhas.Count > 0;
            Closing += delegate(object s, CancelEventArgs e)
            {
                if (!m_blnGravando) return;
                e.Cancel = true;
                MessageBox.Show(this, "Espere a gravação terminar.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            };
        }

        private void AoFechar(object sender, RoutedEventArgs e) { Close(); }

        private void AoMarcarSoAlta(object sender, RoutedEventArgs e)
        {
            foreach (TLinhaDisco l in m_lstLinhas) l.blnAplicar = l.objOpcao != null && (m_chkSoAlta.IsChecked != true || l.strConfianca == "alta");
            AtualizarResumo();
        }

        // ============================================================ ler e casar

        private void AoLer(object sender, RoutedEventArgs e)
        {
            string strArtista = m_txtArtista.Text.Trim();
            if (strArtista.Length == 0) { Status("Informe o artista.", true); return; }
            m_btnLer.IsEnabled = false;
            m_btnAplicar.IsEnabled = false;
            Status("pedindo a discografia ao ARCA Central…", false);
            Log("Discografia: " + strArtista);

            Task.Factory.StartNew(delegate
            {
                TDiscografiaDto d = null; Exception objErro = null;
                try
                {
                    DateTime dtInicio = DateTime.Now;
                    while (true)
                    {
                        d = ClienteCentral.Discografia(strArtista);
                        if (d.blnPronta || d.strErro != null) break;
                        TDiscografiaDto p = d;
                        Dispatcher.BeginInvoke((Action)delegate
                        {
                            Status(p.intTotal > 0
                                ? string.Format("O notebook está lendo a discografia de {0} no Discogs: {1} de {2} discos. Só na primeira vez; depois fica guardada.", p.strArtista, p.intLidos, p.intTotal)
                                : "O notebook está " + p.strFase + "…", false);
                        });
                        if (DateTime.Now - dtInicio > TimeSpan.FromMinutes(30)) throw new TimeoutException("a leitura da discografia passou de 30 minutos");
                        Thread.Sleep(3000);
                    }
                    if (d.strErro == null)
                    {
                        List<TFaixaEntrada> lstE = m_lstLinhas.Select(l => l.objEntrada).ToList();
                        List<List<TOpcaoDisco>> lstOp = CasadorDiscografia.Opcoes(lstE, d.lstDiscos);
                        TOpcaoDisco[] vet = CasadorDiscografia.Escolher(lstE, lstOp);
                        Dispatcher.BeginInvoke((Action)delegate { MostrarCasamento(d, lstOp, vet); });
                        return;
                    }
                }
                catch (Exception ex) { objErro = ex; }
                Dispatcher.BeginInvoke((Action)delegate
                {
                    m_btnLer.IsEnabled = true;
                    string s = objErro != null ? objErro.Message : d.strErro;
                    Status(s, true);
                    Log("ERRO: " + s);
                });
            });
        }

        private void MostrarCasamento(TDiscografiaDto d, List<List<TOpcaoDisco>> lstOp, TOpcaoDisco[] vet)
        {
            m_objDiscografia = d;
            m_txtArtista.Text = d.strArtista;
            for (int i = 0; i < m_lstLinhas.Count; i++)
            {
                List<object> l = new List<object>(lstOp[i].Cast<object>());
                l.Add(TLinhaDisco.NAO_MEXER);
                m_lstLinhas[i].lstOpcoes = l;
                m_lstLinhas[i].objEscolhida = (object)vet[i] ?? TLinhaDisco.NAO_MEXER;
                if (m_chkSoAlta.IsChecked == true && m_lstLinhas[i].strConfianca != "alta") m_lstLinhas[i].blnAplicar = false;
            }
            // na ordem dos discos propostos; as sem disco no fim
            List<TLinhaDisco> lstOrdem = m_lstLinhas.OrderBy(l => l.objOpcao == null ? 1 : 0)
                .ThenBy(l => l.objOpcao == null ? 0 : l.objOpcao.objDisco.intAno)
                .ThenBy(l => l.objOpcao == null ? "" : l.objOpcao.objDisco.strTitulo)
                .ThenBy(l => l.objOpcao == null ? 0 : l.objOpcao.objDisco.intMasterId)
                .ThenBy(l => l.objOpcao == null ? 0 : l.objOpcao.intPosicao).ToList();
            m_lstLinhas.Clear();
            m_lstLinhas.AddRange(lstOrdem);
            m_grade.Items.Refresh();

            int intAlta = m_lstLinhas.Count(l => l.strConfianca == "alta"), intSem = m_lstLinhas.Count(l => l.objOpcao == null);
            Log(string.Format("Discografia de {0}: {1} discos. Casadas: {2} de {3} (alta {4}, média {5}, baixa {6}).",
                d.strArtista, d.lstDiscos.Count, m_lstLinhas.Count - intSem, m_lstLinhas.Count, intAlta,
                m_lstLinhas.Count(l => l.strConfianca == "média"), m_lstLinhas.Count(l => l.strConfianca == "baixa")));
            AtualizarResumo();
            m_btnLer.IsEnabled = true;
            m_btnAplicar.IsEnabled = m_lstLinhas.Any(l => l.objOpcao != null);
            Status(string.Format("{0} de {1} faixas com disco proposto ({2} sem disco). Confira — sobretudo as de confiança média e baixa — e clique em Aplicar.",
                m_lstLinhas.Count - intSem, m_lstLinhas.Count, intSem), false);
        }

        private void AtualizarResumo()
        {
            if (m_objDiscografia == null) return;
            var grupos = m_lstLinhas.Where(l => l.blnAplicar && l.objOpcao != null).GroupBy(l => l.objOpcao.objDisco)
                .OrderBy(g => g.Key.intAno).ThenBy(g => g.Key.strTitulo).ToList();
            m_lblResumo.Text = grupos.Count == 0 ? "Nenhuma faixa marcada."
                : string.Join("\n", grupos.Select(g => string.Format("{0} · {1} — {2} de {3} faixas", g.Key.intAno, NomeDoAlbum(g.Key), g.Count(), g.Key.lstTitulos.Count)).ToArray())
                  + string.Format("\n\n{0} disco(s), {1} faixa(s) marcadas.", grupos.Count, grupos.Sum(g => g.Count()));
        }

        private string NomeDoAlbum(TDiscoDto d) { return OrganizadorDiscografia.NomeDoAlbum(d, m_objDiscografia); }

        // ============================================================ aplicar

        private void AoAplicar(object sender, RoutedEventArgs e)
        {
            m_grade.CommitEdit(DataGridEditingUnit.Row, true);
            List<IGrouping<TDiscoDto, TLinhaDisco>> lstGrupos = m_lstLinhas.Where(l => l.blnAplicar && l.objOpcao != null)
                .GroupBy(l => l.objOpcao.objDisco).OrderBy(g => g.Key.intAno).ToList();
            if (lstGrupos.Count == 0) { Status("Nenhuma faixa marcada.", true); return; }
            bool blnCapas = m_chkCapas.IsChecked == true, blnMover = m_chkMover.IsChecked == true;
            string strArtista = m_objDiscografia.strArtista;
            if (MessageBox.Show(this, string.Format(
                    "Gravar {0} faixa(s) em {1} disco(s) de {2}:\n\n  álbum, ano, número e título de cada faixa, como no Discogs\n  {3}\n  {4}\n\n" +
                    "O áudio não é tocado: cada faixa é gravada numa cópia, conferida (SHA-256 do áudio) e só então trocada.\n" +
                    "Tudo fica registrado no ledger do acervo.",
                    lstGrupos.Sum(g => g.Count()), lstGrupos.Count, strArtista,
                    blnCapas ? "capa de cada disco (embutida; Folder.jpg onde a pasta for de um disco só)" : "sem mexer nas capas",
                    blnMover ? "cada disco movido para a sua pasta" : "as faixas ficam nas pastas onde estão"),
                    Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            // pastas que são inteiras de um disco só (contando também as faixas desmarcadas)
            Dictionary<string, HashSet<int>> dicDiscosDaPasta = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (TLinhaDisco l in m_lstLinhas)
            {
                string p = Path.GetDirectoryName(l.objFaixa.strCaminho);
                if (!dicDiscosDaPasta.ContainsKey(p)) dicDiscosDaPasta[p] = new HashSet<int>();
                dicDiscosDaPasta[p].Add(l.blnAplicar && l.objOpcao != null ? l.objOpcao.objDisco.intMasterId : -1);
            }
            List<TDiscoDto> lstDiscos = lstGrupos.Select(g => g.Key).ToList();
            Dictionary<TDiscoDto, List<TLinhaDisco>> dicLinhas = lstGrupos.ToDictionary(g => g.Key, g => g.ToList());
            Dictionary<TDiscoDto, string> dicNome = lstDiscos.ToDictionary(d => d, d => NomeDoAlbum(d));

            m_blnGravando = true;
            m_btnAplicar.IsEnabled = m_btnLer.IsEnabled = m_btnFechar.IsEnabled = false;
            Action<string> actLog = delegate(string s) { Dispatcher.BeginInvoke((Action)delegate { Log(s); }); Programa.Registrar(s); };

            Task.Factory.StartNew(delegate
            {
                int intOk = 0, intTotal = dicLinhas.Values.Sum(l => l.Count), intDisco = 0;
                foreach (TDiscoDto d in lstDiscos)
                {
                    intDisco++;
                    List<TLinhaDisco> lst = dicLinhas[d];
                    int n = intDisco;
                    Dispatcher.BeginInvoke((Action)delegate { Status(string.Format("disco {0} de {1}: {2} · {3}… (não feche a janela)", n, lstDiscos.Count, d.intAno, dicNome[d]), false); });
                    try { intOk += OrganizadorDiscografia.GravarDisco(d, dicNome[d], lst, strArtista, blnCapas, blnMover, dicDiscosDaPasta, actLog); }
                    catch (Exception ex)
                    {
                        actLog("FALHA no disco " + d.intAno + " · " + d.strTitulo + ": " + ex.Message + " (as faixas dele não foram alteradas)");
                        foreach (TLinhaDisco l in lst) l.strResultado = "não gravada: " + ex.Message;
                    }
                }
                Programa.Registrar("discografia: " + intOk + " de " + intTotal + " faixa(s) gravada(s)");
                Dispatcher.BeginInvoke((Action)delegate
                {
                    m_blnGravando = false;
                    m_btnFechar.IsEnabled = m_btnLer.IsEnabled = true;
                    m_grade.Items.Refresh();
                    Status(string.Format("{0} de {1} faixa(s) gravada(s), áudio conferido. O WMP mostra as mudanças sozinho.{2}", intOk, intTotal,
                        intOk == intTotal ? "" : " Veja a coluna Resultado e o registro abaixo."), intOk != intTotal);
                });
            });
        }

        // ============================================================ utilidades

        private void Status(string s, bool blnAlarme)
        {
            m_lblStatus.Text = s;
            m_lblStatus.Foreground = blnAlarme ? Recurso("Alarme", Brushes.Red) : Recurso("Texto", Brushes.Black);
        }

        private void Log(string s)
        {
            m_txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + Environment.NewLine);
            m_txtLog.ScrollToEnd();
        }

        private Brush Recurso(string k, Brush p) { return (TryFindResource(k) as Brush) ?? p; }
    }
}
