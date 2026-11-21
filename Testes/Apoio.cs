using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ARCA.Identificar.Testes
{
    /// <summary>O que vários testes usam: pasta temporária, MP3 sintético, o ARCA Central, a thread de UI.</summary>
    internal static class Apoio
    {
        /// <summary>O acervo como o Vista o vê. Os testes de integração só LEEM daqui.</summary>
        /// <summary>O acervo de música dos testes (só leitura): ARCA_TESTE_ACERVO ou D:\Músicas.</summary>
        public static readonly string ACERVO = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ARCA_TESTE_ACERVO")) ? @"D:\Músicas" : Environment.GetEnvironmentVariable("ARCA_TESTE_ACERVO");

        /// <summary>Pasta nova em %TEMP% (regra do Lorenzo: "deleta o teste, sempre testes novos").</summary>
        public static string PastaNova()
        {
            string s = Path.Combine(Path.GetTempPath(), "ARCA_TESTE_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(s);
            return s;
        }

        public static void Apagar(string strPasta)
        {
            try
            {
                foreach (string f in Directory.GetFiles(strPasta, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(strPasta, true);
            }
            catch { }
        }

        /// <summary>Ledger temporário: os testes nunca escrevem no ledger do acervo.</summary>
        public static string LedgerDeTeste(string strPasta)
        {
            string s = Path.Combine(strPasta, "ledger-teste.jsonl");
            GravadorMp3.strLedgerForcado = s;
            return s;
        }

        // ---------------------------------------------------------------- MP3 sintético

        /// <summary>N quadros MPEG-1 Layer III, 128 kbps, 44,1 kHz (417 bytes cada), com conteúdo determinístico.</summary>
        public static byte[] QuadrosMpeg(int intQuadros, int intSemente)
        {
            Random r = new Random(intSemente);
            byte[] b = new byte[intQuadros * 417];
            for (int i = 0; i < intQuadros; i++)
            {
                int p = i * 417;
                b[p] = 0xFF; b[p + 1] = 0xFB; b[p + 2] = 0x90; b[p + 3] = 0x64;
                for (int k = 4; k < 417; k++) b[p + k] = (byte)r.Next(256);
            }
            return b;
        }

        /// <summary>Uma ID3v2.3 com os quadros de texto dados (nome de 4 caracteres; "TT2\0" fica como está, para a tag quebrada).</summary>
        public static byte[] Id3v23(params KeyValuePair<string, string>[] vetQuadros)
        {
            MemoryStream ms = new MemoryStream();
            foreach (KeyValuePair<string, string> q in vetQuadros)
            {
                byte[] txt = Encoding.GetEncoding(28591).GetBytes(q.Value);
                ms.Write(Encoding.ASCII.GetBytes(q.Key), 0, 4);
                int n = txt.Length + 1;
                ms.Write(new[] { (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n, (byte)0, (byte)0 }, 0, 6);
                ms.WriteByte(0);   // ISO-8859-1
                ms.Write(txt, 0, txt.Length);
            }
            ms.Write(new byte[64], 0, 64);   // preenchimento
            byte[] corpo = ms.ToArray();
            int t = corpo.Length;
            byte[] cab = { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0, (byte)((t >> 21) & 0x7F), (byte)((t >> 14) & 0x7F), (byte)((t >> 7) & 0x7F), (byte)(t & 0x7F) };
            return cab.Concat(corpo).ToArray();
        }

        /// <summary>Uma ID3v1 (128 bytes) com o título dado.</summary>
        public static byte[] Id3v1(string strTitulo)
        {
            byte[] b = new byte[128];
            b[0] = (byte)'T'; b[1] = (byte)'A'; b[2] = (byte)'G';
            byte[] t = Encoding.ASCII.GetBytes(strTitulo);
            Array.Copy(t, 0, b, 3, Math.Min(30, t.Length));
            return b;
        }

        public static KeyValuePair<string, string> Q(string k, string v) { return new KeyValuePair<string, string>(k, v); }

        // ---------------------------------------------------------------- ARCA Central

        public static bool CentralNoAr()
        {
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(ClienteCentral.Endereco + "/saude");
                req.Proxy = null; req.Timeout = 4000;
                using (WebResponse r = req.GetResponse()) return true;
            }
            catch { return false; }
        }

        public static void ExigirCentral()
        {
            if (!CentralNoAr()) Assert.Inconclusive("o ARCA Central não respondeu em " + ClienteCentral.Endereco + " (o notebook está ligado, com o cabo, e o Central aberto?)");
        }

        public static void ExigirArquivo(string s)
        {
            if (!File.Exists(s) && !Directory.Exists(s)) Assert.Inconclusive("falta no acervo: " + s);
        }

        // ---------------------------------------------------------------- thread de UI (STA)

        private static Dispatcher s_objUi;
        private static readonly object s_objTrava = new object();

        /// <summary>
        /// Executa na thread STA dos testes de janela. É uma só, com o App (e o
        /// Tema.xaml) criado nela: os recursos do WPF pertencem à thread que os criou.
        /// </summary>
        public static void NaUi(Action act)
        {
            lock (s_objTrava)
            {
                if (s_objUi == null)
                {
                    ManualResetEvent ok = new ManualResetEvent(false);
                    Thread t = new Thread(delegate()
                    {
                        if (Application.Current == null) { App app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; }
                        s_objUi = Dispatcher.CurrentDispatcher;
                        // Exceção solta numa janela (BeginInvoke depois de fechar, por exemplo) não pode matar a
                        // thread de UI: os testes seguintes ficariam esperando para sempre. Fica anotada e o teste acusa.
                        s_objUi.UnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
                        {
                            s_strErroSolto = e.Exception.GetType().Name + ": " + e.Exception.Message;
                            e.Handled = true;
                        };
                        ok.Set();
                        Dispatcher.Run();
                    });
                    t.SetApartmentState(ApartmentState.STA);
                    t.IsBackground = true;
                    t.Start();
                    ok.WaitOne();
                }
            }
            Exception objErro = null;
            s_strErroSolto = null;
            bool blnTerminou = false;
            s_objUi.Invoke(DispatcherPriority.Normal, TimeSpan.FromSeconds(90), (Action)delegate
            {
                try { act(); } catch (Exception ex) { objErro = ex; }
                blnTerminou = true;
            });
            if (!blnTerminou) throw new TimeoutException("a janela não respondeu em 90 s (thread de UI presa)");
            if (objErro != null) throw new Exception("na thread de UI: " + objErro.Message, objErro);
            if (s_strErroSolto != null) throw new Exception("exceção solta na thread de UI: " + s_strErroSolto);
        }

        private static volatile string s_strErroSolto;

        /// <summary>Deixa a janela desenhar (processa a fila do dispatcher por um tempo).</summary>
        public static void Esperar(int intMs)
        {
            DispatcherFrame f = new DispatcherFrame();
            DispatcherTimer t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(intMs) };
            t.Tick += delegate { t.Stop(); f.Continue = false; };
            t.Start();
            Dispatcher.PushFrame(f);
        }
    }
}
