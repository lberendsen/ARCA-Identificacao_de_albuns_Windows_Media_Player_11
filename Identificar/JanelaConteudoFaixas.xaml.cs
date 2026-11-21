using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ARCA.Modelo;

namespace ARCA.Identificar
{
    /// <summary>Uma faixa e a classe dela (antes e depois).</summary>
    public sealed class TLinhaConteudo : INotifyPropertyChanged
    {
        public static readonly string[] NOMES = { "Música", "Audiolivro", "Fala" };
        public event PropertyChangedEventHandler PropertyChanged;
        private void Avisar(string s) { if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(s)); }

        public string strCaminho { get; set; }
        public string strArquivo { get { return Path.GetFileName(strCaminho); } }
        public string strTitulo { get; set; }
        public ConteudoFaixa eAntes { get; set; }
        public string strAntes { get { return NOMES[(int)eAntes]; } }
        public string[] lstOpcoes { get { return NOMES; } }
        private string m_strDepois;
        public string strDepois { get { return m_strDepois; } set { m_strDepois = value; Avisar("strDepois"); } }
        public ConteudoFaixa eDepois { get { return (ConteudoFaixa)Math.Max(0, Array.IndexOf(NOMES, m_strDepois)); } }
        private string m_strResultado;
        public string strResultado { get { return m_strResultado; } set { m_strResultado = value; Avisar("strResultado"); } }
    }

    /// <summary>
    /// Música, audiolivro ou fala, em XAML
    /// (XAML, editável no VS 2010), agora também para MP3. Grava a classe de
    /// mídia que o WMP usa (WM/MediaClass*; no MP3, quadros PRIV), por faixa,
    /// pela cópia conferida, e registra no ledger.
    /// </summary>
    public partial class JanelaConteudoFaixas : Window
    {
        private readonly List<TLinhaConteudo> m_lst = new List<TLinhaConteudo>();
        private bool m_blnGravando;

        public JanelaConteudoFaixas(List<string> lstArquivos)
        {
            InitializeComponent();
            foreach (string s in lstArquivos.Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || TFaixaMp3.EhWma(f)))
            {
                string strTitulo = "";
                try { strTitulo = TFaixaMp3.Ler(s).strTitulo; } catch { }
                ConteudoFaixa e = GravadorMp3.LerClasse(s);
                m_lst.Add(new TLinhaConteudo { strCaminho = s, strTitulo = strTitulo, eAntes = e, strDepois = TLinhaConteudo.NOMES[(int)e] });
            }
            m_grade.ItemsSource = m_lst;
            m_lblStatus.Text = m_lst.Count + " faixa(s). Escolha a classe de cada uma e clique em Aplicar; só as que mudam são gravadas.";
            Closing += delegate(object s, CancelEventArgs e) { if (m_blnGravando) e.Cancel = true; };
        }

        private void Todas(ConteudoFaixa e) { foreach (TLinhaConteudo l in m_lst) l.strDepois = TLinhaConteudo.NOMES[(int)e]; }
        private void AoTudoMusica(object sender, RoutedEventArgs e) { Todas(ConteudoFaixa.Musica); }
        private void AoTudoAudiolivro(object sender, RoutedEventArgs e) { Todas(ConteudoFaixa.Audiolivro); }
        private void AoTudoFala(object sender, RoutedEventArgs e) { Todas(ConteudoFaixa.Fala); }
        private void AoFechar(object sender, RoutedEventArgs e) { Close(); }

        private void AoAplicar(object sender, RoutedEventArgs e)
        {
            m_grade.CommitEdit();
            List<TLinhaConteudo> lstMuda = m_lst.Where(l => l.eDepois != l.eAntes).ToList();
            if (lstMuda.Count == 0) { m_lblStatus.Text = "Nenhuma faixa muda de classe."; return; }
            m_blnGravando = true;
            m_btnAplicar.IsEnabled = false;
            m_lblStatus.Text = "gravando " + lstMuda.Count + " faixa(s)…";
            Task.Factory.StartNew(delegate
            {
                int intOk = 0;
                foreach (TLinhaConteudo l in lstMuda)
                {
                    try
                    {
                        GravadorMp3.GravarClasse(l.strCaminho, l.eDepois, l.eAntes);
                        l.strResultado = "gravada · áudio intacto";
                        l.eAntes = l.eDepois;
                        intOk++;
                    }
                    catch (Exception ex) { l.strResultado = "NÃO gravada: " + ex.Message; }
                }
                Programa.Registrar("classe de mídia: " + intOk + " de " + lstMuda.Count + " faixa(s)");
                Dispatcher.BeginInvoke((Action)delegate
                {
                    m_blnGravando = false;
                    m_btnAplicar.IsEnabled = true;
                    m_grade.Items.Refresh();
                    m_lblStatus.Text = string.Format("{0} de {1} faixa(s) gravada(s), áudio conferido. O WMP passa a mostrar os audiolivros à parte.", intOk, lstMuda.Count);
                });
            });
        }
    }
}
