using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ARCA.Identificar.Testes
{
    /// <summary>Leitura de tags, inclusive a tag quebrada do iTunes antigo (quadros v2.2 dentro de ID3v2.3), que nem o taglib nem o WMP leem.</summary>
    [TestClass]
    public class TesteTags
    {
        private string m_strPasta;
        [TestInitialize] public void Antes() { m_strPasta = Apoio.PastaNova(); }
        [TestCleanup] public void Depois() { Apoio.Apagar(m_strPasta); }

        [TestMethod, TestCategory("Unidade")]
        public void TagQuebrada_TituloArtistaAlbumNumero_SaoLidos()
        {
            string s = Path.Combine(m_strPasta, "16 Idade Da Pedra.mp3");
            File.WriteAllBytes(s, Apoio.Id3v23(Apoio.Q("TT2\0", "Idade Da Pedra"), Apoio.Q("TP1\0", "Beatriz Azevedo"),
                                               Apoio.Q("TAL\0", "Mapa-Mundi [Samba And Poetry]"), Apoio.Q("TRK\0", "16/17"))
                                  .Concat(Apoio.QuadrosMpeg(40, 5)).ToArray());
            TFaixaMp3 f = TFaixaMp3.Ler(s);
            Assert.AreEqual("Idade Da Pedra", f.strTitulo);
            Assert.AreEqual("Beatriz Azevedo", f.strArtista);
            Assert.AreEqual("Mapa-Mundi [Samba And Poetry]", f.strAlbum);
            Assert.AreEqual(16, f.intFaixaTag);
        }

        [TestMethod, TestCategory("Unidade")]
        public void TagNormal_ELidaPeloTaglib()
        {
            string s = Path.Combine(m_strPasta, "x.mp3");
            File.WriteAllBytes(s, Apoio.Id3v23(Apoio.Q("TIT2", "Será"), Apoio.Q("TPE1", "Legião Urbana"), Apoio.Q("TALB", "Dois"))
                                  .Concat(Apoio.QuadrosMpeg(40, 6)).ToArray());
            TFaixaMp3 f = TFaixaMp3.Ler(s);
            Assert.AreEqual("Será", f.strTitulo);
            Assert.AreEqual("Legião Urbana", f.strArtista);
            Assert.AreEqual("Dois", f.strAlbum);
        }

        [TestMethod, TestCategory("Unidade")]
        public void EhWma_PelaExtensao()
        {
            Assert.IsTrue(TFaixaMp3.EhWma(@"C:\a\b.WMA"));
            Assert.IsFalse(TFaixaMp3.EhWma(@"C:\a\b.mp3"));
        }

        [TestMethod, TestCategory("Unidade")]
        public void Formatar_Duracao()
        {
            Assert.AreEqual("4:06", TFaixaMp3.Formatar(246));
            Assert.AreEqual("1:02:03", TFaixaMp3.Formatar(3723));
            Assert.AreEqual("", TFaixaMp3.Formatar(0));
        }
    }
}
