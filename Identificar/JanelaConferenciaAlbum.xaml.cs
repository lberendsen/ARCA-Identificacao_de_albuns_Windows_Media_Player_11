using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using ARCA.Modelo;

namespace ARCA.Identificar
{
    /// <summary>Uma linha da grade da conferência.</summary>
    public sealed class TLinhaConferencia
    {
        public string strDisco { get; set; }
        public string strNumero { get; set; }
        public string strArquivo { get; set; }
        public string strSituacao { get; set; }
        public Brush objCor { get; set; }
    }

    /// <summary>
    /// Confere no AccurateRip um álbum que já está no acervo, sem o disco — a
    /// em XAML (editável no VS 2010).
    /// Só lê; gravar o veredito nas faixas é o botão à parte.
    /// </summary>
    public partial class JanelaConferenciaAlbum : Window
    {
        private readonly List<string> m_lstArquivos;
        private List<TDiscoConferido> m_lstDiscos = new List<TDiscoConferido>();
        private int m_intSemIndice;
        private volatile bool m_blnFechando;
        private bool m_blnOcupado;

        public JanelaConferenciaAlbum(List<string> lstArquivos)
        {
            InitializeComponent();
            m_lstArquivos = lstArquivos;
            string strPasta = lstArquivos.Count > 0 ? Path.GetDirectoryName(lstArquivos[0]) : "";
            m_lblAlbum.Text = strPasta + " · " + lstArquivos.Count(TFaixaMp3.EhWma) + " faixa(s) WMA";
            m_lblEstado.Text = "";
            Closing += delegate(object s, System.ComponentModel.CancelEventArgs e) { m_blnFechando = true; };
            Loaded += delegate
            {
                m_blnOcupado = true;
                new Thread(Conferir) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
            };
        }

        private void Estado(string s) { Dispatcher.BeginInvoke((Action)delegate { if (!m_blnFechando) m_lblEstado.Text = s; }); }

        private void Conferir()
        {
            try
            {
                Estado("lendo o índice do CD gravado em cada faixa…");
                m_lstDiscos = ConferenciaAccurateRip.Agrupar(m_lstArquivos, out m_intSemIndice);
                for (int i = 0; i < m_lstDiscos.Count; i++)
                {
                    string strQual = m_lstDiscos.Count > 1 ? string.Format("disco {0} de {1}: ", i + 1, m_lstDiscos.Count) : "";
                    TDiscoConferido d = m_lstDiscos[i];
                    ConferenciaAccurateRip.Conferir(d, s => Estado(strQual + s), () => m_blnFechando);
                    if (d.objResultado != null && d.objResultado.strFalha == null)
                    {
                        try { ConferenciaAccurateRip.Registrar(d, Path.GetDirectoryName(d.lstArquivos[0])); }
                        catch (Exception ex) { Programa.Registrar("conferência: ledger: " + ex.Message); }
                    }
                }
                Dispatcher.BeginInvoke((Action)Mostrar);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Programa.Registrar("conferência AccurateRip: " + ex);
                Dispatcher.BeginInvoke((Action)delegate { if (!m_blnFechando) { m_blnOcupado = false; m_lblEstado.Text = ""; m_lblVeredito.Text = "A conferência falhou: " + ex.Message; } });
            }
        }

        private Brush Cor(string k, Brush p) { return (TryFindResource(k) as Brush) ?? p; }

        private void Mostrar()
        {
            if (m_blnFechando) return;
            m_blnOcupado = false;
            m_lblEstado.Text = "";
            Brush objOk = new SolidColorBrush(Color.FromRgb(0x1E, 0x7B, 0x34)), objAlarme = Cor("Alarme", Brushes.Red), objFraco = Cor("TextoFraco", Brushes.Gray);
            List<TLinhaConferencia> lst = new List<TLinhaConferencia>();
            int intFaixas = 0, intConferem = 0, intForaDaBase = 0, intProblemas = 0;
            for (int i = 0; i < m_lstDiscos.Count; i++)
            {
                TDiscoConferido d = m_lstDiscos[i];
                string strDisco = (i + 1).ToString();
                if (d.objResultado == null)
                {
                    intProblemas++;
                    foreach (string f in d.lstArquivos)
                        lst.Add(new TLinhaConferencia { strDisco = strDisco, strArquivo = Path.GetFileName(f), strSituacao = d.strProblema, objCor = objAlarme });
                    continue;
                }
                TResultadoAccurateRip r = d.objResultado;
                if (!r.blnNaBase) intForaDaBase++;
                for (int t = 0; t < r.lstFaixas.Count; t++)
                {
                    TFaixaAccurateRip f = r.lstFaixas[t];
                    intFaixas++;
                    if (f.Confere) intConferem++;
                    lst.Add(new TLinhaConferencia
                    {
                        strDisco = strDisco, strNumero = f.intNumero.ToString(), strArquivo = t < d.lstArquivos.Count ? Path.GetFileName(d.lstArquivos[t]) : "",
                        strSituacao = !r.blnNaBase ? "disco fora da base" : f.Confere ? string.Format("confere ({0}, confiança {1})", f.strVersao, f.intConfianca)
                                    : string.Format("NÃO confere ({0} registro(s) na base)", f.intTotalNaBase),
                        objCor = !r.blnNaBase ? objFraco : f.Confere ? objOk : objAlarme
                    });
                }
            }
            m_grade.ItemsSource = lst;

            string strObs = string.Join("; ", m_lstDiscos.Where(d => !string.IsNullOrEmpty(d.strObservacao)).Select(d => d.strObservacao).ToArray());
            if (m_lstDiscos.Count == 0)
                m_lblVeredito.Text = "Estas faixas não guardam o índice do CD de origem (WM/MCDI): sem ele não há como conferir sem o disco."
                                     + (m_intSemIndice > 0 ? " (" + m_intSemIndice + " faixa(s) WMA sem índice.)" : "");
            else if (intProblemas > 0 && intFaixas == 0)
                m_lblVeredito.Text = "Não deu para conferir: " + string.Join("; ", m_lstDiscos.Where(d => d.strProblema != null).Select(d => d.strProblema).ToArray());
            else if (intForaDaBase == m_lstDiscos.Count)
                m_lblVeredito.Text = "Este disco não está no AccurateRip: não há como comprovar sem o disco. A ripagem pode estar perfeita — só não há com o que comparar.";
            else if (intConferem == intFaixas)
            {
                m_lblVeredito.Text = string.Format("BIT-PERFECT COMPROVADO: as {0} faixas conferem com o AccurateRip.", intFaixas);
                m_lblVeredito.Foreground = objOk;
            }
            else
            {
                m_lblVeredito.Text = string.Format("{0} de {1} faixa(s) conferem. As que não conferem provavelmente têm erro de leitura da época em que foram ripadas; " +
                                                   "só ripando o CD de novo dá para corrigir.", intConferem, intFaixas);
                m_lblVeredito.Foreground = objAlarme;
            }
            if (strObs.Length > 0) m_lblVeredito.Text += "\n(" + strObs + ")";
            m_btnGravar.IsEnabled = m_lstDiscos.Any(d => d.objResultado != null && d.objResultado.blnNaBase);
        }

        private void AoGravar(object sender, RoutedEventArgs e)
        {
            int intGravadas = 0;
            List<string> lstFalhas = new List<string>();
            foreach (TDiscoConferido d in m_lstDiscos)
            {
                if (d.objResultado == null || !d.objResultado.blnNaBase) continue;
                List<string> lst = ConferenciaAccurateRip.GravarVeredito(d);
                lstFalhas.AddRange(lst);
                intGravadas += Math.Min(d.objResultado.lstFaixas.Count, d.lstArquivos.Count) - lst.Count;
            }
            m_btnGravar.IsEnabled = false;
            m_lblEstado.Text = lstFalhas.Count == 0
                ? string.Format("Veredito gravado em {0} faixa(s), no atributo ARCA/Verification.", intGravadas)
                : string.Format("Gravado em {0} faixa(s). Não consegui gravar em: {1} (a faixa pode estar tocando).", intGravadas, string.Join(", ", lstFalhas.ToArray()));
            Programa.Registrar("conferência: veredito gravado em " + intGravadas + " faixa(s)");
        }

        private void AoFechar(object sender, RoutedEventArgs e)
        {
            if (m_blnOcupado && MessageBox.Show(this, "A conferência ainda está rodando. Parar?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            Close();
        }
    }
}
