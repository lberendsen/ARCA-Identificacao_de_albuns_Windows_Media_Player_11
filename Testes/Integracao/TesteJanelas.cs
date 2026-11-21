using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using ARCA.Modelo;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ARCA.Identificar.Testes
{
    /// <summary>
    /// Cada janela da identificação abre (o XAML carrega, os controles com nome
    /// existem, o código preenche) com faixas reais, só leitura. Nada é gravado:
    /// as janelas são fechadas sem Aplicar.
    /// </summary>
    [TestClass]
    public class TesteJanelas
    {
        private static readonly string IBRAHIM = Apoio.ACERVO + @"\Ibrahim Ferrer\Buenos Hermanos";
        private static readonly string UNKNOWN = Apoio.ACERVO + @"\MP3\Unknown Artist\Unknown Album";

        private static List<string> Arquivos(string d, int n)
        {
            Apoio.ExigirArquivo(d);
            return Directory.GetFiles(d).Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || TFaixaMp3.EhWma(f)).OrderBy(f => f).Take(n).ToList();
        }

        private static void Abrir(Func<Window> fabrica)
        {
            Apoio.NaUi(delegate
            {
                Window w = fabrica();
                w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -2000; w.Top = 0; w.ShowInTaskbar = false;
                w.Show();
                Apoio.Esperar(600);
                Assert.IsTrue(w.ActualWidth > 100 && w.ActualHeight > 100, w.GetType().Name + " não desenhou");
                w.Close();
            });
        }

        private static TCandidatoIdentificacao Candidato(string strFonte, int n)
        {
            TCandidatoIdentificacao c = new TCandidatoIdentificacao { strFonte = strFonte, strArtista = "Artista", strAlbum = "Álbum", strAno = "2000" };
            for (int i = 0; i < n; i++) { c.lstTitulos.Add("Faixa " + (i + 1)); c.lstArtistasFaixas.Add(""); c.lstDuracoes.Add(200 + i); }
            return c;
        }

        private static BitmapSource Imagem()
        {
            BitmapSource b = BitmapSource.Create(600, 400, 300, 300, System.Windows.Media.PixelFormats.Bgr32, null, new byte[600 * 400 * 4], 600 * 4);
            b.Freeze();
            return b;
        }

        [TestMethod, TestCategory("Integracao")] public void Identificar() { Abrir(() => new JanelaIdentificar(Arquivos(IBRAHIM, 13))); }
        [TestMethod, TestCategory("Integracao")] public void SepararDesconhecido() { Abrir(() => new JanelaSepararDesconhecido(Arquivos(UNKNOWN, 30))); }
        [TestMethod, TestCategory("Integracao")] public void ConteudoFaixas() { Abrir(() => new JanelaConteudoFaixas(Arquivos(IBRAHIM, 5))); }
        [TestMethod, TestCategory("Integracao")] public void CapaEncarte() { Abrir(() => new JanelaCapaEncarte(Arquivos(IBRAHIM, 5))); }
        [TestMethod, TestCategory("Integracao")] public void Digitalizar() { Abrir(() => new JanelaDigitalizar()); }
        [TestMethod, TestCategory("Integracao")] public void Corte() { Abrir(() => new JanelaCorteImagem(Imagem(), "Cortar", true)); }
        [TestMethod, TestCategory("Integracao")] public void Divisao() { Abrir(() => new JanelaDivisaoImagem(Imagem(), "Dividir")); }

        [TestMethod, TestCategory("Integracao")]
        public void EscolhaFonte_QuatroFontes()
        {
            Abrir(() => new JanelaEscolhaFonte(new List<KeyValuePair<string, TCandidatoIdentificacao>> {
                new KeyValuePair<string, TCandidatoIdentificacao>("MusicBrainz · pelo índice do CD", Candidato("MusicBrainz", 13)),
                new KeyValuePair<string, TCandidatoIdentificacao>("GnuDB · pelo índice do CD", Candidato("GnuDB", 13)),
                new KeyValuePair<string, TCandidatoIdentificacao>("Apple Music", Candidato("Apple Music", 12)),
                new KeyValuePair<string, TCandidatoIdentificacao>("Discogs", null) },
                Enumerable.Range(0, 13).Select(i => 200 + i).ToList(), null, "teste"));
        }

        [TestMethod, TestCategory("Integracao")]
        public void CatalogarAMao() { Abrir(() => new JanelaEditorFaixas(new List<int> { 200, 210, 220 }, "Artista", "Álbum", new List<string> { "a", "b", "c" }, null, null)); }

        [TestMethod, TestCategory("Integracao")]
        public void Discografia()
        {
            List<string> lst = Arquivos(Apoio.ACERVO + @"\MP3\Roberto Carlos\1963", 3);
            lst.AddRange(Arquivos(Apoio.ACERVO + @"\MP3\Roberto Carlos\1966 - Roberto Carlos (1966)", 3));
            Abrir(() => new JanelaDiscografia(lst));
        }

        [TestMethod, TestCategory("Integracao")]
        public void ConferenciaAlbum_AbreEPara()
        {
            // Ao abrir, a conferência começa numa thread; fechar a interrompe (nada é gravado).
            Abrir(() => new JanelaConferenciaAlbum(Arquivos(IBRAHIM, 13)));
        }

        [TestMethod, TestCategory("Integracao")]
        public void Ripar_AbreSemComecar()
        {
            // Sem começar o rip: só o layout. A pasta de entrada nem é criada.
            string strPasta = Apoio.PastaNova();
            try
            {
                Abrir(() => new JanelaRipar('E', strPasta, Apoio.ACERVO, false));
                Assert.AreEqual(0, Directory.GetDirectories(strPasta).Length, "a janela criou pasta sem começar");
            }
            finally { Apoio.Apagar(strPasta); }
        }

        [TestMethod, TestCategory("Integracao")]
        public void Ripar_ReabreCopiaFeita_PeloRelatorio()
        {
            // Uma pasta de entrada como a cópia deixa depois do Aplicar: relatório e índice, sem imagem nem WMA.
            string strPasta = Apoio.PastaNova();
            try
            {
                TRelatorioExtracao r = new TRelatorioExtracao { strDrive = "ASUS DRW-24F1MT", intOffsetAplicado = 6, strOrigemOffset = "tabela do AccurateRip", strAccurateRip = "na base" };
                for (int i = 1; i <= 3; i++)
                {
                    TVerificacaoFaixa v = r.Verificacao(i);
                    v.intConfiancaAr = i == 3 ? 0 : 5; v.intTotalAr = 5; v.strVersaoAr = "v2"; v.blnWmaIdentico = true;
                    if (i == 3) v.intReleituraDiferentes = 0;
                }
                r.Gravar(strPasta);
                File.WriteAllText(Path.Combine(strPasta, "AudioCD.cue"),
                    "REM SESSION 1\r\nTITLE \"Disco\"\r\nPERFORMER \"Artista\"\r\nFILE \"AudioCD.bin\" BINARY\r\n" +
                    "  TRACK 01 AUDIO\r\n    TITLE \"Um\"\r\n    INDEX 01 00:00:00\r\n" +
                    "  TRACK 02 AUDIO\r\n    TITLE \"Dois\"\r\n    INDEX 01 02:00:00\r\n" +
                    "  TRACK 03 AUDIO\r\n    TITLE \"Tres\"\r\n    INDEX 01 05:30:00\r\n" +
                    "REM LEAD-OUT 09:00:00\r\nREM ARCA-LEADOUT-EXATO\r\n");
                Apoio.NaUi(delegate
                {
                    JanelaRipar w = JanelaRipar.DeCopiaFeita(strPasta, Apoio.ACERVO);
                    w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = -2000; w.Top = 0; w.ShowInTaskbar = false;
                    w.Show();
                    Apoio.Esperar(600);
                    System.Windows.Controls.DataGrid g = (System.Windows.Controls.DataGrid)w.FindName("m_grade");
                    List<TLinhaRip> lst = ((IEnumerable<TLinhaRip>)g.ItemsSource).ToList();
                    w.Close();
                    Assert.AreEqual(3, lst.Count, "faixas na grade");
                    Assert.AreEqual("2:00", lst[0].strDuracao); Assert.AreEqual("3:30", lst[2].strDuracao, "a última faixa vai até o lead-out do índice");
                    Assert.AreEqual("BIT-PERFECT", lst[0].strVeredito);
                    Assert.AreEqual("lida duas vezes, idêntica", lst[2].strVeredito);
                    Assert.AreEqual("idêntica", lst[2].strReleitura);
                });
            }
            finally { Apoio.Apagar(strPasta); }
        }
    }
}
