using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ARCA.Modelo;

namespace ARCA.Identificar
{
    /// <summary>Uma linha da grade do rip.</summary>
    public sealed class TLinhaRip
    {
        public int intFaixa;
        public string strNumero { get; set; }
        public string strTitulo { get; set; }
        public string strDuracao { get; set; }
        public string strAccurateRip { get; set; }
        public string strReleitura { get; set; }
        public string strWma { get; set; }
        public string strVeredito { get; set; }
        public Brush objCor { get; set; }
    }

    /// <summary>
    /// A cópia segura de um CD (Ripador.cs), com o andamento e o veredito de cada faixa.
    /// Ao fim, se tudo saiu comprovado, abre o Identificar com as faixas da pasta de entrada;
    /// o "Aplicar às faixas" de lá é que leva o disco ao acervo. Se alguma faixa não saiu
    /// comprovada, para e mostra: levar ao acervo assim é decisão do usuário (o botão).
    /// Layout em XAML (editável no VS 2010).
    /// </summary>
    public partial class JanelaRipar : Window
    {
        private readonly TRipador m_objRip;
        private readonly string m_strAcervo;
        private List<TLinhaRip> m_lstLinhas = new List<TLinhaRip>();
        private volatile bool m_blnRipando;
        private bool m_blnIdentificarAberto;

        /// <summary>Para os testes: não começa sozinho (blnComecar = false).</summary>
        public JanelaRipar(char chrLetra, string strEntrada, string strAcervo, bool blnComecar = true)
            : this(chrLetra, TRipador.PastaNova(strEntrada), strAcervo, blnComecar, 0) { }

        private JanelaRipar(char chrLetra, string strPastaDoDisco, string strAcervo, bool blnComecar, int intNada)
        {
            InitializeComponent();
            m_strAcervo = strAcervo;
            m_objRip = new TRipador(chrLetra, strPastaDoDisco);
            m_lblLeitor.Text = string.Format("Leitor {0}: · lido pelo SCSI, conferido no AccurateRip, WMA Lossless idêntico ao disco · acervo: {1}", chrLetra, strAcervo);
            m_lblPasta.Text = "Entrada (fora do acervo): " + m_objRip.strPasta;
            m_lblPasta.ToolTip = m_objRip.strPasta;
            m_grade.ItemsSource = m_lstLinhas;

            m_objRip.objLog = delegate(string s) { Na(delegate { Log(s); }); };
            m_objRip.objFase = delegate(string a, string b) { Na(delegate { m_lblFase.Text = Capitalizar(a); m_lblDetalhe.Text = b ?? ""; }); };
            m_objRip.objProgresso = delegate(string a, double p, string d)
            {
                Na(delegate { m_barra.Value = Math.Max(0, Math.Min(100, p)); m_lblDetalhe.Text = d ?? ""; });
            };
            m_objRip.objEtapa = delegate(int n) { Na(delegate { AoConcluirEtapa(n); }); };

            Closing += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                if (!m_blnRipando) return;
                if (MessageBox.Show(this, "A cópia ainda está em andamento. Interromper agora?\n\nO que já foi lido fica na pasta de entrada, incompleto; nada vai para o acervo.",
                                    "ARCA — copiar o CD", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                Programa.Registrar("rip interrompido pelo usuário: " + m_objRip.strPasta);
                Environment.Exit(2);   // a leitura não tem ponto de parada: o processo sai, o leitor fica livre
            };
            if (blnComecar) Loaded += delegate { Comecar(); };
        }

        public TRipador Ripador { get { return m_objRip; } }

        /// <summary>
        /// Reabre o resultado de uma cópia já feita, pelo que ficou na pasta de entrada (o relatório
        /// ARCA_extracao.txt, o índice AudioCD.cue e o log). "ARCAIdentificar.exe /ripar-ver &lt;pasta&gt;".
        /// Só lê. Se as faixas ainda estão lá (não foram aplicadas), o botão Identificar as leva.
        /// </summary>
        public static JanelaRipar DeCopiaFeita(string strPasta, string strAcervo)
        {
            JanelaRipar j = new JanelaRipar('E', strPasta, strAcervo, false, 0);
            TRipador r = j.m_objRip;
            r.objRelatorio = TRelatorioExtracao.Ler(strPasta);
            if (r.objRelatorio == null) { j.Log("Não há relatório de cópia em " + strPasta); return j; }
            long lngFim;
            r.lstFaixasCue = ProcessadorExtracaoAudio.ParsearCue(r.Cue, out r.strAlbumCdText, out r.strArtistaCdText, out lngFim);
            j.m_lngFimDoAudio = lngFim;
            r.strFabricante = r.objRelatorio.strDrive; r.strModelo = "";
            r.intOffset = r.objRelatorio.intOffsetAplicado;
            r.lstWma = Directory.GetFiles(strPasta, "*.wma").OrderBy(f => f).ToList();
            string strLog = Path.Combine(strPasta, "aquisicao_detalhada.log");
            if (File.Exists(strLog))
                foreach (string l in File.ReadAllLines(strLog))
                    if (l.Contains("] [INFO] ") || l.Contains("] [AVISO] ") || l.Contains("] [ERRO] ")) j.m_txtLog.AppendText(l + Environment.NewLine);
            j.Loaded += delegate
            {
                j.AoConcluirEtapa(1); j.AoConcluirEtapa(2); j.AoConcluirEtapa(4);
                j.AoTerminar(true, false);
                j.m_lblFase.Text = "Cópia de " + Directory.GetCreationTime(strPasta).ToString("dd/MM/yyyy HH:mm");
                if (r.lstWma.Count == 0)
                {
                    j.m_lblDetalhe.Text = "as faixas já foram levadas ao acervo";
                    j.m_btnIdentificar.IsEnabled = false;
                }
            };
            return j;
        }

        /// <summary>Onde o áudio acaba, em bytes, quando a imagem já foi apagada (lido do índice).</summary>
        private long m_lngFimDoAudio;

        private void Na(Action a) { Dispatcher.BeginInvoke(a); }

        private void Comecar()
        {
            m_blnRipando = true;
            m_btnFechar.Content = "Interromper";
            Destacar(1);
            Log("Pasta de entrada: " + m_objRip.strPasta);
            new Thread(delegate()
            {
                bool blnOk = m_objRip.Ripar();
                Na(delegate { AoTerminar(blnOk); });
            }) { IsBackground = true, Name = "rip" }.Start();
        }

        private void AoConcluirEtapa(int n)
        {
            if (n == 1)
            {
                long lngBin = 0;
                try { lngBin = File.Exists(m_objRip.Bin) ? new FileInfo(m_objRip.Bin).Length : m_lngFimDoAudio; } catch { }
                List<TrackInfo> lst = m_objRip.lstFaixasCue.Where(t => t.TrackType == "AUDIO").OrderBy(t => t.ByteOffset).ToList();
                m_lstLinhas = new List<TLinhaRip>();
                for (int i = 0; i < lst.Count; i++)
                {
                    long lngFim = i + 1 < lst.Count ? lst[i + 1].ByteOffset : lngBin;
                    double dblSeg = Math.Max(0, lngFim - lst[i].ByteOffset) / 176400.0;
                    m_lstLinhas.Add(new TLinhaRip
                    {
                        intFaixa = lst[i].TrackNum,
                        strNumero = lst[i].TrackNum.ToString("00"),
                        strTitulo = string.IsNullOrEmpty(lst[i].Title) ? "Faixa " + lst[i].TrackNum : lst[i].Title,
                        strDuracao = string.Format("{0}:{1:00}", (int)dblSeg / 60, (int)dblSeg % 60),
                        strAccurateRip = "…", strReleitura = "", strWma = "", strVeredito = "lida",
                        objCor = Recurso("TextoFraco", Brushes.Gray)
                    });
                }
                if (!string.IsNullOrEmpty(m_objRip.strAlbumCdText))
                    m_lblTitulo.Text = "Copiar o CD — " + (string.IsNullOrEmpty(m_objRip.strArtistaCdText) ? "" : m_objRip.strArtistaCdText + " — ") + m_objRip.strAlbumCdText;
                m_lblLeitor.Text = string.Format("Leitor {0}: {1} {2} · {3} faixa(s) · acervo: {4}", m_objRip.chrLetra,
                    (m_objRip.strFabricante ?? "").Trim(), (m_objRip.strModelo ?? "").Trim(), m_lstLinhas.Count, m_strAcervo);
                Destacar(2);
            }
            else if (n == 2)
            {
                m_lblLeitor.Text += string.Format(" · offset {0:+0;-0;0}", m_objRip.intOffset);
                Destacar(3);
            }
            else if (n == 4) Destacar(4);
            Atualizar();
        }

        /// <summary>A grade a partir do relatório (AccurateRip, releitura, WMA).</summary>
        private void Atualizar()
        {
            TRelatorioExtracao r = m_objRip.objRelatorio;
            if (r == null) return;
            foreach (TLinhaRip l in m_lstLinhas)
            {
                TVerificacaoFaixa v;
                if (!r.dicVerificacao.TryGetValue(l.intFaixa, out v)) continue;
                l.strAccurateRip = v.intConfiancaAr > 0 ? string.Format("confere (confiança {0})", v.intConfiancaAr)
                                 : v.intConfiancaAr == 0 && v.intTotalAr > 0 ? "NÃO confere"
                                 : v.intConfiancaAr == 0 ? "fora da base"
                                 : r.strAccurateRip != null && r.strAccurateRip.StartsWith("não consultado") ? "não consultado" : "…";
                l.strReleitura = v.intReleituraDiferentes < 0 ? (v.intConfiancaAr > 0 ? "dispensada" : "")
                               : v.intReleituraDiferentes == 0 && v.intReleituraIlegiveis == 0 ? "idêntica"
                               : string.Format("{0} setor(es) diferente(s)", v.intReleituraDiferentes + v.intReleituraIlegiveis);
                l.strWma = v.blnWmaIdentico == null ? "" : v.blnWmaIdentico.Value ? "idêntico ao disco" : "DIFERENTE";
                if (v.intMascarados > 0) l.strReleitura += (l.strReleitura.Length > 0 ? " · " : "") + v.intMascarados + " ilegível(is)";
                if (v.blnWmaIdentico != null)
                {
                    NivelBitPerfect k = v.Nivel;
                    l.strVeredito = k == NivelBitPerfect.Comprovada ? "BIT-PERFECT" : k == NivelBitPerfect.LidaDuasVezes ? "lida duas vezes, idêntica" : "NÃO comprovada: " + v.Motivo();
                    l.objCor = k == NivelBitPerfect.NaoComprovada ? Recurso("Alarme", Brushes.Red) : Recurso("Acento", Brushes.Navy);
                }
            }
            m_grade.ItemsSource = null;
            m_grade.ItemsSource = m_lstLinhas;
        }

        private void AoTerminar(bool blnOk) { AoTerminar(blnOk, true); }

        private void AoTerminar(bool blnOk, bool blnAbrirIdentificar)
        {
            m_blnRipando = false;
            m_btnFechar.Content = "Fechar";
            m_barra.Value = blnOk ? 100 : m_barra.Value;
            Atualizar();
            m_lblVeredito.Visibility = Visibility.Visible;
            if (!blnOk)
            {
                m_lblFase.Text = "A cópia não terminou";
                m_lblVeredito.Text = "Falhou: " + m_objRip.strFalha + "\nNada foi para o acervo. O registro detalhado está em aquisicao_detalhada.log, na pasta de entrada.";
                m_lblVeredito.Foreground = Recurso("Alarme", Brushes.Red);
                return;
            }
            m_lblFase.Text = "Cópia concluída";
            m_lblDetalhe.Text = m_objRip.lstWma.Count + " faixa(s) em WMA Lossless";
            m_btnIdentificar.IsEnabled = true;
            if (m_objRip.Exata)
            {
                m_lblVeredito.Text = "Bit-perfect: " + m_objRip.objRelatorio.ResumoBitPerfect() + ".\nAbrindo o Identificar: o disco só vai para o acervo quando você clicar em \"Aplicar às faixas\".";
                m_lblVeredito.Foreground = Recurso("Acento", Brushes.Navy);
                if (blnAbrirIdentificar) Na(delegate { AbrirIdentificar(); });
                else m_lblVeredito.Text = "Bit-perfect: " + m_objRip.objRelatorio.ResumoBitPerfect() + ".";
            }
            else
            {
                m_lblVeredito.Text = "Atenção — " + (m_objRip.objRelatorio != null ? m_objRip.objRelatorio.ResumoBitPerfect() : "sem conferência") +
                                     ".\nVeja na grade quais faixas não saíram comprovadas. Você pode limpar o disco e copiar de novo, ou levar ao acervo assim (cada faixa leva o veredito no atributo ARCA/Verification).";
                m_lblVeredito.Foreground = Recurso("Alarme", Brushes.Red);
            }
        }

        private void AbrirIdentificar()
        {
            if (m_objRip.lstWma.Count == 0) return;
            JanelaIdentificar j = new JanelaIdentificar(m_objRip.lstWma)
            {
                strRaizParaMover = m_strAcervo,
                blnAnoNaPasta = false,          // o acervo é Artista\Álbum, como o WMP organiza
                blnIdentificarAoAbrir = true,   // as faixas têm o índice do CD: a busca começa sozinha
                strEventoAoAplicar = "cópia segura do CD (WMP › Copiar do CD › ARCA) · " +
                                     (m_objRip.objRelatorio != null ? m_objRip.objRelatorio.ResumoBitPerfect() : "sem conferência") +
                                     " · leitor " + ((m_objRip.strFabricante ?? "") + " " + (m_objRip.strModelo ?? "")).Trim() +
                                     string.Format(" (offset {0:+0;-0;0})", m_objRip.intOffset) + " · entrada " + m_objRip.strPasta
            };
            j.Closed += delegate { m_blnIdentificarAberto = false; };
            m_blnIdentificarAberto = true;
            j.Show();
            Destacar(4);
        }

        private void AoIdentificar(object sender, RoutedEventArgs e)
        {
            if (m_blnIdentificarAberto) { foreach (Window w in Application.Current.Windows) if (w is JanelaIdentificar) w.Activate(); return; }
            AbrirIdentificar();
        }

        private void AoAbrirPasta(object sender, RoutedEventArgs e)
        {
            try { if (Directory.Exists(m_objRip.strPasta)) Process.Start("explorer.exe", "\"" + m_objRip.strPasta + "\""); }
            catch (Exception ex) { Log("não abri a pasta: " + ex.Message); }
        }

        private void AoFechar(object sender, RoutedEventArgs e) { Close(); }

        private void Destacar(int n)
        {
            TextBlock[] vet = { m_lblEtapa1, m_lblEtapa2, m_lblEtapa3, m_lblEtapa4 };
            for (int i = 0; i < vet.Length; i++)
            {
                vet[i].Foreground = i + 1 == n ? Recurso("Acento", Brushes.Navy) : Recurso(i + 1 < n ? "Texto" : "TextoFraco", Brushes.Gray);
                vet[i].FontWeight = i + 1 == n ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        private static string Capitalizar(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.TrimEnd('…', '.').Trim().ToLowerInvariant();
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        public void Log(string strTexto)
        {
            m_txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + strTexto + Environment.NewLine);
            m_txtLog.ScrollToEnd();
        }

        private Brush Recurso(string strChave, Brush objPadrao)
        {
            return (TryFindResource(strChave) as Brush) ?? objPadrao;
        }
    }
}
