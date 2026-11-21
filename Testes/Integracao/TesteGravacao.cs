using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ARCA.Modelo;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ARCA.Identificar.Testes
{
    /// <summary>
    /// Gravação de verdade, numa CÓPIA NOVA de faixas reais do acervo em %TEMP%,
    /// apagada no fim (regra do Lorenzo). O ledger é o de teste. Confere o que
    /// importa: o áudio não muda, as tags e a capa ficam, o arquivo vai para a
    /// pasta do disco com o nome "NN Título", a classe de mídia é gravada e lida.
    /// </summary>
    [TestClass]
    public class TesteGravacao
    {
        public static readonly string MP3_REAL = Apoio.ACERVO + @"\MP3\Legião Urbana\V\10 Come Share My Life.mp3";
        public static readonly string WMA_REAL = Apoio.ACERVO + @"\Moby\18\04 A grande fuga.wma";
        public const string CAPA = @"D:\DEV\ARCA-WMP\Sondas\TesteCapaMp3\capa.jpg";

        private string m_strPasta;
        [TestInitialize] public void Antes() { m_strPasta = Apoio.PastaNova(); Apoio.LedgerDeTeste(m_strPasta); }
        [TestCleanup] public void Depois() { GravadorMp3.strLedgerForcado = null; Apoio.Apagar(m_strPasta); }

        private string Copia(string strReal, string strSubpasta)
        {
            Apoio.ExigirArquivo(strReal);
            string d = Path.Combine(m_strPasta, strSubpasta);
            Directory.CreateDirectory(d);
            string c = Path.Combine(d, Path.GetFileName(strReal));
            File.Copy(strReal, c);
            File.SetAttributes(c, FileAttributes.Normal);
            return c;
        }

        private TDadosAlbum Album(byte[] arrCapa)
        {
            return new TDadosAlbum { strArtistaAlbum = "Artista Teste", strAlbum = "Álbum Teste ARCA", strAno = "1999", strGenero = "MPB", intTotalFaixas = 12,
                                     strFonte = "teste", arrCapa = arrCapa, strOrigemCapa = "arquivo", strEvento = "teste automático", blnRenomearAoMover = true };
        }

        private void GravarEConferir(string strReal, string strSub)
        {
            string c = Copia(strReal, strSub);
            string strHashAntes = TFaixaMp3.HashDoAudio(strReal);
            TFaixaMp3 f = TFaixaMp3.Ler(c);
            f.intPosicaoNova = 6; f.strTituloNovo = "Título: com/barra"; f.strArtistaNovo = ""; f.strCompositorNovo = "Compositor Teste"; f.blnAplicar = true;
            string strDestino = Path.Combine(m_strPasta, @"Destino\1999 - Álbum Teste ARCA");
            List<string> lstLog = new List<string>();
            int n = GravadorMp3.Aplicar(new List<TFaixaMp3> { f }, Album(File.Exists(CAPA) ? File.ReadAllBytes(CAPA) : null), lstLog.Add, delegate { }, strDestino, true);
            Assert.AreEqual(1, n, string.Join(" | ", lstLog.ToArray()));
            string strFinal = Path.Combine(strDestino, "07 Título - com - barra" + Path.GetExtension(strReal).ToLowerInvariant());
            Assert.IsTrue(File.Exists(strFinal), "não foi movida/renomeada: " + string.Join(" | ", lstLog.ToArray()));
            Assert.IsFalse(File.Exists(c), "o original ficou na pasta de origem");
            Assert.AreEqual(strHashAntes, TFaixaMp3.HashDoAudio(strFinal), "o áudio mudou");
            TFaixaMp3 g = TFaixaMp3.Ler(strFinal);
            Assert.AreEqual("Título: com/barra", g.strTitulo);
            Assert.AreEqual("Álbum Teste ARCA", g.strAlbum);
            Assert.AreEqual(7, g.intFaixaTag);
            Assert.AreEqual(1999u, g.uintAno);
            if (File.Exists(CAPA)) Assert.IsTrue(g.blnTemCapa, "sem capa embutida");
            Assert.IsTrue(File.ReadAllText(GravadorMp3.strLedgerForcado).Contains("teste automático"), "não registrou no ledger de teste");
            Assert.IsFalse(Directory.GetFiles(m_strPasta, "*.arca-partial", SearchOption.AllDirectories).Any(), "sobrou cópia parcial");
        }

        [TestMethod, TestCategory("Integracao"), Timeout(300000)]
        public void Mp3_GravaMoveRenomeia_AudioIntacto() { GravarEConferir(MP3_REAL, "mp3"); }

        [TestMethod, TestCategory("Integracao"), Timeout(300000)]
        public void Wma_GravaMoveRenomeia_AudioIntacto() { GravarEConferir(WMA_REAL, "wma"); }

        [TestMethod, TestCategory("Integracao"), Timeout(300000)]
        public void ClasseDeMidia_Mp3EWma_GravaELe()
        {
            foreach (string strReal in new[] { MP3_REAL, WMA_REAL })
            {
                string c = Copia(strReal, "classe-" + Path.GetExtension(strReal).Trim('.'));
                string strHash = TFaixaMp3.HashDoAudio(c);
                Assert.AreEqual(ConteudoFaixa.Musica, GravadorMp3.LerClasse(c));
                GravadorMp3.GravarClasse(c, ConteudoFaixa.Audiolivro, ConteudoFaixa.Musica);
                Assert.AreEqual(ConteudoFaixa.Audiolivro, GravadorMp3.LerClasse(c), Path.GetExtension(c));
                GravadorMp3.GravarClasse(c, ConteudoFaixa.Fala, ConteudoFaixa.Audiolivro);
                Assert.AreEqual(ConteudoFaixa.Fala, GravadorMp3.LerClasse(c), Path.GetExtension(c));
                GravadorMp3.GravarClasse(c, ConteudoFaixa.Musica, ConteudoFaixa.Fala);
                Assert.AreEqual(ConteudoFaixa.Musica, GravadorMp3.LerClasse(c), Path.GetExtension(c));
                Assert.AreEqual(strHash, TFaixaMp3.HashDoAudio(c), "o áudio mudou: " + Path.GetExtension(c));
            }
        }

        [TestMethod, TestCategory("Integracao")]
        public void IndiceDoCd_LidoDoWmaReal()
        {
            string d = Apoio.ACERVO + @"\Ibrahim Ferrer\Buenos Hermanos";
            Apoio.ExigirArquivo(d);
            Assert.AreEqual(TesteIndiceDoCd.MCDI_BUENOS_HERMANOS, TFaixaMp3.IndiceDoCd(Directory.GetFiles(d, "*.wma").OrderBy(s => s).First()));
        }
    }
}
