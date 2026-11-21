using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ARCA.Central;
using ARCA.Modelo;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ARCA.Identificar.Testes
{
    /// <summary>O índice do CD (WM/MCDI): leitura, Disc ID do MusicBrainz e o arquivo da base do AccurateRip.</summary>
    [TestClass]
    public class TesteIndiceDoCd
    {
        /// <summary>O índice que o WMP gravou no "Buenos Hermanos" (Ibrahim Ferrer), 13 faixas.</summary>
        public const string MCDI_BUENOS_HERMANOS = "D+96+3F51+909E+EBD1+13CEE+180BD+1C7FE+21B38+259ED+2A134+3001E+34EE3+39259+3CECD";

        [TestMethod, TestCategory("Unidade")]
        public void DeMcdi_FaixasEFim()
        {
            TTocAudio t = TTocAudio.DeMcdi(MCDI_BUENOS_HERMANOS, 0);
            Assert.AreEqual(13, t.QtdFaixas);
            Assert.AreEqual(0, t.lstInicios[0]);                 // 0x96 = 150 setores de pré-gap
            Assert.AreEqual(0x3CECD - 150, t.intLeadOut);
        }

        [TestMethod, TestCategory("Unidade")]
        public void DiscId_ConfereComOMusicBrainz()
        {
            // O MusicBrainz achou o lançamento exato por este ID (log do Central, 20/11/2026).
            Assert.AreEqual("lcq7n3tklbQDmlEHR4UyPjawJ8g-", ConsultaToc.DiscId(TTocAudio.DeMcdi(MCDI_BUENOS_HERMANOS, 0)));
        }

        [TestMethod, TestCategory("Unidade")]
        public void McdiDeBytes_TextoUtf16_ComoOWmpGravaNoMp3()
        {
            Assert.AreEqual("2+96+34BC+57E4", TFaixaMp3.McdiDeBytes(Encoding.Unicode.GetBytes("2+96+34BC+57E4\0")));
        }

        [TestMethod, TestCategory("Unidade")]
        public void McdiDeBytes_CdromTocBinario_EmMsf()
        {
            // CDROM_TOC: tamanho, primeira e última faixa; 8 bytes por faixa (endereço MSF), e o lead-out (0xAA).
            byte[] toc = {
                0, 26, 1, 2,
                0, 0x10, 1, 0, 0, 0, 2, 0,       // faixa 1: 00:02:00 = 150
                0, 0x10, 2, 0, 0, 3, 0, 0,       // faixa 2: 03:00:00 = 13500
                0, 0x10, 0xAA, 0, 0, 5, 0, 0 };  // lead-out: 05:00:00 = 22500
            Assert.AreEqual("2+96+34BC+57E4", TFaixaMp3.McdiDeBytes(toc));
        }

        [TestMethod, TestCategory("Unidade")]
        public void AccurateRip_Interpretar_SoOBlocoDesteDisco()
        {
            TTocAudio t = TTocAudio.DeMcdi("2+96+34BC+57E4", 0);
            uint id1, id2, idF;
            AccurateRip.CalcularIds(t, out id1, out id2, out idF);
            MemoryStream ms = new MemoryStream();
            Action<int, uint, uint, uint, uint[]> bloco = (n, a, b, c, crcs) =>
            {
                ms.WriteByte((byte)n);
                ms.Write(BitConverter.GetBytes(a), 0, 4); ms.Write(BitConverter.GetBytes(b), 0, 4); ms.Write(BitConverter.GetBytes(c), 0, 4);
                foreach (uint crc in crcs) { ms.WriteByte(7); ms.Write(BitConverter.GetBytes(crc), 0, 4); ms.Write(new byte[4], 0, 4); }
            };
            bloco(2, id1, id2, idF, new uint[] { 0x11111111, 0x22222222 });
            bloco(2, id1 + 1, id2, idF, new uint[] { 0x33333333, 0x44444444 });   // outro disco: ignorado
            string strFalha;
            List<AccurateRip.TPrensagem> lst = ConferenciaAccurateRip.Interpretar(ms.ToArray(), t, out strFalha);
            Assert.AreEqual(1, lst.Count);
            Assert.AreEqual(0x22222222u, lst[0].arrCrc[1]);
            Assert.AreEqual(7, lst[0].arrConfianca[0]);
            Assert.IsNull(strFalha);
        }
    }
}
