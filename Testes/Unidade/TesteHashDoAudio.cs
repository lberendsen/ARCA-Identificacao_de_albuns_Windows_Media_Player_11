using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ARCA.Identificar.Testes
{
    /// <summary>
    /// A trava de segurança de toda gravação: o hash é só do áudio. Mudar tag
    /// (no começo ou no fim, quantas houver) não pode mudar o hash; mudar um byte
    /// do áudio tem de mudar.
    /// </summary>
    [TestClass]
    public class TesteHashDoAudio
    {
        private string m_strPasta;
        [TestInitialize] public void Antes() { m_strPasta = Apoio.PastaNova(); }
        [TestCleanup] public void Depois() { Apoio.Apagar(m_strPasta); }

        private string Gravar(string strNome, params byte[][] partes)
        {
            string s = Path.Combine(m_strPasta, strNome);
            File.WriteAllBytes(s, partes.SelectMany(p => p).ToArray());
            return s;
        }

        [TestMethod, TestCategory("Unidade")]
        public void TagsDiferentes_MesmoAudio_MesmoHash()
        {
            byte[] audio = Apoio.QuadrosMpeg(30, 1);
            string a = Gravar("a.mp3", Apoio.Id3v23(Apoio.Q("TIT2", "Um")), audio);
            string b = Gravar("b.mp3", Apoio.Id3v23(Apoio.Q("TIT2", "Outro título bem maior"), Apoio.Q("TPE1", "Artista")), audio, Apoio.Id3v1("Fim"));
            Assert.AreEqual(TFaixaMp3.HashDoAudio(a), TFaixaMp3.HashDoAudio(b));
        }

        [TestMethod, TestCategory("Unidade")]
        public void VariasId3v1Empilhadas_SaoTodasDescontadas()
        {
            // "Une nuit d'amour" (Slaï): três ID3v1 seguidas no fim; o taglib deixa uma só ao gravar.
            byte[] audio = Apoio.QuadrosMpeg(30, 2);
            string a = Gravar("a.mp3", audio, Apoio.Id3v1("1"), Apoio.Id3v1("2"), Apoio.Id3v1("3"));
            string b = Gravar("b.mp3", audio, Apoio.Id3v1("1"));
            Assert.AreEqual(TFaixaMp3.HashDoAudio(a), TFaixaMp3.HashDoAudio(b));
        }

        [TestMethod, TestCategory("Unidade")]
        public void UmByteDeAudioDiferente_MudaOHash()
        {
            byte[] audio = Apoio.QuadrosMpeg(30, 3), outro = (byte[])audio.Clone();
            outro[5000] ^= 0x01;
            Assert.AreNotEqual(TFaixaMp3.HashDoAudio(Gravar("a.mp3", audio)), TFaixaMp3.HashDoAudio(Gravar("b.mp3", outro)));
        }

        [TestMethod, TestCategory("Unidade")]
        public void CopiaComOutraExtensao_UsaOFormatoDado()
        {
            // "faixa.wma.arca-partial" não tem a extensão: o formato vem do original (sonda S7).
            byte[] audio = Apoio.QuadrosMpeg(10, 4);
            string a = Gravar("x.mp3", audio), b = Gravar("x.mp3.arca-partial", audio);
            Assert.AreEqual(TFaixaMp3.HashDoAudio(a, false), TFaixaMp3.HashDoAudio(b, false));
        }
    }
}
