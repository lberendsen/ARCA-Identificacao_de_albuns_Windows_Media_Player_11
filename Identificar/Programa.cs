using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Markup;

namespace ARCA.Identificar
{
    /// <summary>
    /// ARCA Identificar — aberto pela ponte do WMP ("Localizar Informações sobre
    /// o Álbum" ou "Enviar para › ARCA"). Recebe um arquivo-lista (uma faixa por
    /// linha, UTF-8), ou, para testes, uma pasta.
    /// </summary>
    public static class Programa
    {
        public static string PastaDoPrograma { get { return AppDomain.CurrentDomain.BaseDirectory; } }

        public static string ArquivoDeLog
        {
            get
            {
                string strPasta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ARCA");
                Directory.CreateDirectory(strPasta);
                return Path.Combine(strPasta, "identificar.log");
            }
        }

        /// <summary>Onde o ARCA Ripar põe os discos copiados até a aprovação: fora do acervo e das pastas que o WMP vigia.</summary>
        public static string PastaDeEntrada { get { return Configuracao("PastaDeEntrada", @"D:\ARCA\Entrada"); } }

        /// <summary>A raiz do acervo (Artista\Álbum), para onde o "Aplicar às faixas" leva o disco copiado.</summary>
        public static string Acervo { get { return Configuracao("Acervo", @"D:\Músicas"); } }

        private static string Configuracao(string strChave, string strPadrao)
        {
            string s = null;
            try { s = System.Configuration.ConfigurationManager.AppSettings[strChave]; } catch { }
            return string.IsNullOrEmpty(s) ? strPadrao : s;
        }

        public static void Registrar(string strTexto)
        {
            try { File.AppendAllText(ArquivoDeLog, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + strTexto + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Registrar("ERRO fatal: " + e.ExceptionObject);
            };
            // Esta máquina não sai para a internet: a base do AccurateRip vem pelo ARCA Central.
            ARCA.Modelo.AccurateRip.objObterBase = ClienteCentral.ArquivoDaBase;
            ARCA.Modelo.OffsetDrive.objObterTabela = ClienteCentral.TabelaDeOffsets;
            // O App.xaml traz o tema (Tema.xaml, compilado junto).
            App app = new App();
            app.InitializeComponent();
            app.Exit += delegate { Registrar("janela fechada"); };
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.DispatcherUnhandledException += delegate(object s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
            {
                Registrar("ERRO não tratado: " + e.Exception);
                MessageBox.Show("Erro inesperado: " + e.Exception.Message + "\n\nDetalhes em " + ArquivoDeLog, "ARCA Identificar",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };

            // "/ripar E:" — o item "ARCA: copiar este CD" do menu Copiar do CD do WMP.
            if (args.Length > 0 && args[0].Equals("/ripar", StringComparison.OrdinalIgnoreCase))
            {
                char chrLetra = args.Length > 1 && args[1].Length > 0 ? char.ToUpperInvariant(args[1][0]) : 'E';
                Registrar("copiar o CD do leitor " + chrLetra + ": · entrada " + PastaDeEntrada + " · acervo " + Acervo);
                // Duas janelas vivem juntas (o rip e o Identificar): o programa acaba quando a última fechar.
                app.ShutdownMode = ShutdownMode.OnLastWindowClose;
                return app.Run(new JanelaRipar(chrLetra, PastaDeEntrada, Acervo));
            }

            // "/ripar-ver <pasta>": rever o resultado de uma cópia já feita (a pasta de entrada).
            if (args.Length > 1 && args[0].Equals("/ripar-ver", StringComparison.OrdinalIgnoreCase))
            {
                app.ShutdownMode = ShutdownMode.OnLastWindowClose;
                return app.Run(JanelaRipar.DeCopiaFeita(args[1], Acervo));
            }

            List<string> lstArquivos = new List<string>();
            try
            {
                string strArg = args.Length > 0 ? args[0] : null;
                if (strArg != null && Directory.Exists(strArg))
                    lstArquivos.AddRange(Directory.GetFiles(strArg));
                else if (strArg != null && File.Exists(strArg))
                {
                    lstArquivos.AddRange(File.ReadAllLines(strArg, Encoding.UTF8).Select(l => l.Trim()).Where(l => l.Length > 0));
                    if (strArg.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) { try { File.Delete(strArg); } catch { } }
                }
            }
            catch (Exception ex) { Registrar("lista de faixas ilegível: " + ex.Message); }

            Registrar("aberto com " + lstArquivos.Count + " arquivo(s): " + string.Join(" | ", lstArquivos.Take(3).ToArray()) + (lstArquivos.Count > 3 ? " …" : ""));
            // Vários álbuns de uma vez (várias pastas): é organização pela discografia do artista.
            int intPastas = lstArquivos.Select(s => Path.GetDirectoryName(s) ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (intPastas > 1)
            {
                Registrar("várias pastas (" + intPastas + "): organizar pela discografia");
                return app.Run(new JanelaDiscografia(lstArquivos));
            }
            // O "Álbum Desconhecido" do WMP (MP3\Unknown Artist\Unknown Album): faixas soltas, a separar em discos.
            string strPasta = lstArquivos.Count > 0 ? Path.GetFileName(Path.GetDirectoryName(lstArquivos[0]) ?? "") : "";
            // Também as pastas que o WMP criou ao ripar CD sem informação: "Álbum Desconhecido (1_3_2006 15_53_37)".
            if (strPasta.StartsWith("Unknown Album", StringComparison.OrdinalIgnoreCase) || strPasta.StartsWith("Álbum Desconhecido", StringComparison.OrdinalIgnoreCase))
            {
                Registrar("Álbum Desconhecido: separar em discos");
                return app.Run(new JanelaSepararDesconhecido(lstArquivos));
            }
            JanelaIdentificar objJanela = new JanelaIdentificar(lstArquivos);
            return app.Run(objJanela);
        }
    }
}
