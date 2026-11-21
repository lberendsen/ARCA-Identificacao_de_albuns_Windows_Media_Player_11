using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ARCA.Interop;
using ARCA.Modelo;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ARCA.Identificar.Testes
{
    /// <summary>
    /// O motor do ARCA Ripar sem o CD: um disco sintético (imagem .bin e índice .cue, como a
    /// extração gera) é cortado em faixas e codificado em WMA Lossless; cada WMA, decodificado,
    /// tem de ser idêntico ao trecho do disco — inclusive com a correção do offset do leitor,
    /// que este teste calcula por conta própria. E o CD-TEXT, com e sem pacote corrompido.
    /// </summary>
    [TestClass]
    public class TesteMotorDeRip
    {
        private const int BYTES_SETOR = 2352;

        // ---------------------------------------------------------------- o disco sintético

        /// <summary>Uma imagem de áudio: setores de PCM estéreo 16 bits, cada faixa com um tom e ruído próprios.</summary>
        private static byte[] Imagem(int[] vetSetores)
        {
            int total = vetSetores.Sum();
            byte[] b = new byte[total * BYTES_SETOR];
            Random r = new Random(7);
            int p = 0;
            for (int f = 0; f < vetSetores.Length; f++)
            {
                int n = vetSetores[f] * BYTES_SETOR / 4;
                for (int i = 0; i < n; i++)
                {
                    double t = i / 44100.0;
                    short e = (short)(8000 * Math.Sin(2 * Math.PI * (220 * (f + 1)) * t) + r.Next(-300, 300));
                    short d = (short)(6000 * Math.Sin(2 * Math.PI * (330 * (f + 1)) * t) + r.Next(-300, 300));
                    b[p++] = (byte)e; b[p++] = (byte)(e >> 8); b[p++] = (byte)d; b[p++] = (byte)(d >> 8);
                }
            }
            return b;
        }

        private static string Msf(int lba) { return string.Format("{0:D2}:{1:D2}:{2:D2}", lba / 75 / 60, lba / 75 % 60, lba % 75); }

        /// <summary>O .cue do jeito que a extração escreve (sessão 1, uma faixa de áudio por TRACK, o lead-out exato).</summary>
        private static string Cue(int[] vetSetores)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("REM SESSION 1\r\nFILE \"AudioCD.bin\" BINARY\r\n");
            int lba = 0;
            for (int f = 0; f < vetSetores.Length; f++)
            {
                sb.AppendFormat("  TRACK {0:D2} AUDIO\r\n    INDEX 01 {1}\r\n", f + 1, Msf(lba));
                lba += vetSetores[f];
            }
            sb.AppendFormat("REM LEAD-OUT {0}\r\nREM ARCA-LEADOUT-EXATO\r\n", Msf(lba));
            return sb.ToString();
        }

        /// <summary>O áudio que a faixa tem de ter: o trecho do disco deslocado pelo offset (fora do disco, silêncio).</summary>
        private static string HashEsperado(byte[] bin, long inicio, long fim, int intOffset)
        {
            long desloc = (long)intOffset * 4;
            byte[] t = new byte[fim - inicio];
            for (long i = 0; i < t.Length; i++)
            {
                long o = inicio + i + desloc;
                t[i] = o >= 0 && o < bin.Length ? bin[o] : (byte)0;
            }
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(t)).Replace("-", "").ToLowerInvariant();
        }

        private static void Cortar(int intOffset)
        {
            string pasta = Apoio.PastaNova();
            try
            {
                int[] vetSetores = { 150, 213, 176 };   // 2 s, 2,84 s e 2,35 s: tamanhos que não caem em bloco redondo
                byte[] bin = Imagem(vetSetores);
                File.WriteAllBytes(Path.Combine(pasta, "AudioCD.bin"), bin);
                File.WriteAllText(Path.Combine(pasta, "AudioCD.cue"), Cue(vetSetores), Encoding.ASCII);
                List<string> lstLog = new List<string>();
                List<TConferenciaWma> lstConf = new List<TConferenciaWma>();
                bool blnOk = ProcessadorExtracaoAudio.FatiarEConverterParaWma(Path.Combine(pasta, "AudioCD.cue"), "Artista de Teste", "Álbum de Teste",
                    null, null, null, m => lstLog.Add(m), null, intOffset, lstConf);
                Console.WriteLine(string.Join("\r\n", lstLog.ToArray()));
                Assert.IsTrue(blnOk, "o corte falhou");
                Assert.AreEqual(3, lstConf.Count, "uma conferência por faixa");
                long inicio = 0;
                for (int f = 0; f < 3; f++)
                {
                    long fim = inicio + (long)vetSetores[f] * BYTES_SETOR;
                    TConferenciaWma c = lstConf.First(x => x.intFaixa == f + 1);
                    Assert.IsTrue(c.Confere, "faixa " + (f + 1) + ": o WMA decodificado não é o trecho do disco — " + c.strFalha);
                    Assert.AreEqual(fim - inicio, c.lngBytesWma, "faixa " + (f + 1) + ": tamanho");
                    Assert.AreEqual(HashEsperado(bin, inicio, fim, intOffset), c.strHashFonte,
                                    "faixa " + (f + 1) + ": o trecho cortado não é o do disco deslocado em " + intOffset + " amostras");
                    Assert.IsTrue(File.Exists(c.strArquivoWma) && c.strArquivoWma.EndsWith(".wma"), "faixa " + (f + 1) + ": o WMA não está lá");
                    inicio = fim;
                }
            }
            finally { Apoio.Apagar(pasta); }
        }

        [TestMethod, TestCategory("Unidade")]
        public void Corte_SemOffset_CadaWmaIgualAoTrechoDoDisco() { Cortar(0); }

        [TestMethod, TestCategory("Unidade")]
        public void Corte_ComOffsetDoLeitor_MaisSeis_DeslocaASeisAmostras() { Cortar(6); }

        [TestMethod, TestCategory("Unidade")]
        public void Corte_ComOffsetNegativo_OInicioDoDiscoViraSilencio() { Cortar(-30); }

        // ---------------------------------------------------------------- CD-TEXT

        /// <summary>Os pacotes de 18 bytes de um tipo (0x80 títulos, 0x81 intérpretes), com o CRC certo.</summary>
        private static List<byte[]> Pacotes(byte bTipo, string[] vetTextos, ref int intSeq)
        {
            List<byte[]> lst = new List<byte[]>();
            Encoding enc = Encoding.GetEncoding(28591);
            List<byte> lstBytes = new List<byte>(); List<int> lstFaixa = new List<int>(), lstPos = new List<int>();
            for (int f = 0; f < vetTextos.Length; f++)
            {
                byte[] t = enc.GetBytes(vetTextos[f]);
                for (int i = 0; i <= t.Length; i++) { lstBytes.Add(i < t.Length ? t[i] : (byte)0); lstFaixa.Add(f); lstPos.Add(i); }
            }
            for (int p = 0; p < lstBytes.Count; p += 12)
            {
                byte[] k = new byte[18];
                k[0] = bTipo; k[1] = (byte)lstFaixa[p]; k[2] = (byte)intSeq++; k[3] = (byte)Math.Min(15, lstPos[p]);
                for (int i = 0; i < 12; i++) k[4 + i] = p + i < lstBytes.Count ? lstBytes[p + i] : (byte)0;
                int crc = 0;
                for (int i = 0; i < 16; i++) { crc ^= k[i] << 8; for (int b = 0; b < 8; b++) crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF; }
                crc ^= 0xFFFF; k[16] = (byte)(crc >> 8); k[17] = (byte)crc;
                lst.Add(k);
            }
            return lst;
        }

        private static byte[] CdText(bool blnCorromper)
        {
            int seq = 0;
            List<byte[]> lst = Pacotes(0x80, new[] { "Mitos do Jazz", "Thelonious", "Epistrophy", "Mysterioso à là" }, ref seq);
            lst.AddRange(Pacotes(0x81, new[] { "Thelonious Monk", "Monk", "Monk", "Monk" }, ref seq));
            if (blnCorromper) lst[1][6] ^= 0x55;   // um byte de texto trocado: o CRC não confere mais
            byte[] arr = new byte[4 + lst.Count * 18];
            for (int i = 0; i < lst.Count; i++) Array.Copy(lst[i], 0, arr, 4 + i * 18, 18);
            return arr;
        }

        [TestMethod, TestCategory("Unidade")]
        public void CdText_TitulosEInterpretes_ComAcento()
        {
            string alb, art, cod; int desc;
            Dictionary<int, string> tit = new Dictionary<int, string>(), arts = new Dictionary<int, string>();
            byte[] a = CdText(false);
            SptiDiscReader.DecodificarCdText(a, a.Length, out alb, out art, tit, arts, out desc, out cod);
            Assert.AreEqual("Mitos do Jazz", alb); Assert.AreEqual("Thelonious Monk", art);
            Assert.AreEqual("Thelonious", tit[1]); Assert.AreEqual("Epistrophy", tit[2]);
            Assert.AreEqual("Mysterioso à là", tit[3]);
            Assert.AreEqual("Monk", arts[2]); Assert.AreEqual(0, desc);
        }

        [TestMethod, TestCategory("Unidade")]
        public void CdText_PacoteCorrompido_EDescartadoEOsSeguintesSeSalvam()
        {
            string alb, art, cod; int desc;
            Dictionary<int, string> tit = new Dictionary<int, string>(), arts = new Dictionary<int, string>();
            byte[] a = CdText(true);
            SptiDiscReader.DecodificarCdText(a, a.Length, out alb, out art, tit, arts, out desc, out cod);
            Console.WriteLine("descartados {0}; títulos: {1}", desc, string.Join(" | ", tit.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value).ToArray()));
            Assert.IsTrue(desc >= 1, "o pacote corrompido devia ser descartado");
            Assert.AreEqual("Epistrophy", tit[2], "a leitura não ressincronizou depois do pacote ruim");
            Assert.AreEqual("Thelonious Monk", art);
        }

        // ---------------------------------------------------------------- o índice

        [TestMethod, TestCategory("Unidade")]
        public void Indice_DoCue_DaOMcdiQueOWmpGrava()
        {
            // O CD do Thelonious Monk (Mitos do Jazz 12): o MCDI montado a partir do .cue tem de
            // ser o que o MusicBrainz reconhece pelo Disc ID (conferido à mão em 21/11/2026).
            int[] lbas = { 0, 13482, 27392, 42400, 56699, 91138, 111465, 146319, 174577, 218863, 236790, 268943 };
            StringBuilder sb = new StringBuilder("REM SESSION 1\r\nFILE \"AudioCD.bin\" BINARY\r\n");
            for (int i = 0; i < lbas.Length; i++) sb.AppendFormat("  TRACK {0:D2} AUDIO\r\n    INDEX 01 {1}\r\n", i + 1, Msf(lbas[i]));
            sb.AppendFormat("REM LEAD-OUT {0}\r\nREM ARCA-LEADOUT-EXATO\r\n", Msf(321333));
            string pasta = Apoio.PastaNova();
            try
            {
                string cue = Path.Combine(pasta, "AudioCD.cue");
                File.WriteAllText(cue, sb.ToString(), Encoding.ASCII);
                TTocAudio t = TTocAudio.DeCue(cue, 321333L * BYTES_SETOR);
                Assert.IsNotNull(t);
                Assert.AreEqual("C+96+3540+6B96+A636+DE11+16498+1B3FF+23C25+2AA87+35785+39D8C+41B25+4E7CB", t.ParaMcdi());
            }
            finally { Apoio.Apagar(pasta); }
        }
    }
}
