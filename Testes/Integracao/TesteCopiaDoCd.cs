using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ARCA.Interop;
using ARCA.Modelo;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ARCA.Identificar.Testes
{
    /// <summary>
    /// A prova de que a cópia segura é repetível: copia de novo o CD que está no leitor
    /// (o mesmo motor que o WMP aciona) numa pasta temporária e compara, amostra a amostra,
    /// cada faixa com a do álbum que já entrou no acervo por uma cópia anterior.
    ///
    /// Leva minutos e precisa do CD: só roda com ARCA_TESTE_COPIA_ALBUM = a pasta do álbum
    /// no acervo (ex.: D:\Músicas\Thelonious Monk\Mitos do jazz, Volume 12 - Thelonious Monk).
    /// Sem ela, fica inconclusivo. O acervo só é lido; a cópia nova é apagada no fim.
    /// </summary>
    [TestClass]
    public class TesteCopiaDoCd
    {
        /// <summary>SHA-256 do áudio decodificado (PCM) de um WMA, e quantos bytes.</summary>
        public static string HashPcm(string strWma, out long lngBytes)
        {
            lngBytes = 0;
            using (LeitorAudio l = new LeitorAudio())
            using (SHA256 sha = SHA256.Create())
            {
                Assert.IsTrue(l.Abrir(strWma), "não abri " + strWma + ": " + l.Falha);
                byte[] b;
                while ((b = l.ProximoBlocoBytes()) != null) { sha.TransformBlock(b, 0, b.Length, null, 0); lngBytes += b.Length; }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                Assert.IsTrue(l.Terminou, "erro decodificando " + strWma + ": " + l.Falha);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        [TestMethod, TestCategory("Integracao"), Timeout(40 * 60 * 1000)]
        public void SegundaCopia_IdenticaAAnterior_AmostraAAmostra()
        {
            string strAlbum = Environment.GetEnvironmentVariable("ARCA_TESTE_COPIA_ALBUM");
            if (string.IsNullOrEmpty(strAlbum)) Assert.Inconclusive("defina ARCA_TESTE_COPIA_ALBUM (a pasta do álbum já copiado) com o mesmo CD no leitor");
            if (!Path.IsPathRooted(strAlbum)) strAlbum = Path.Combine(Apoio.ACERVO, strAlbum);   // relativo ao acervo (sem acento na linha de comando)
            Assert.IsTrue(Directory.Exists(strAlbum), "não existe: " + strAlbum);
            List<string> lstAcervo = Directory.GetFiles(strAlbum, "*.wma").OrderBy(f => f).ToList();
            Assert.IsTrue(lstAcervo.Count > 0, "o álbum não tem WMA");

            Apoio.ExigirCentral();
            AccurateRip.objObterBase = ClienteCentral.ArquivoDaBase;
            OffsetDrive.objObterTabela = ClienteCentral.TabelaDeOffsets;
            string strLetra = Environment.GetEnvironmentVariable("ARCA_TESTE_LEITOR");
            string strPasta = Apoio.PastaNova();
            try
            {
                TRipador r = new TRipador(string.IsNullOrEmpty(strLetra) ? 'E' : strLetra[0], Path.Combine(strPasta, "copia"));
                r.objLog = s => Console.WriteLine(s);
                DateTime dt = DateTime.Now;
                Assert.IsTrue(r.Ripar(), "a cópia falhou: " + r.strFalha);
                Console.WriteLine("cópia em {0:F0} s · {1}", (DateTime.Now - dt).TotalSeconds, r.objRelatorio.ResumoBitPerfect());
                Assert.AreEqual(lstAcervo.Count, r.lstWma.Count, "número de faixas");
                Assert.IsTrue(r.Exata, "a segunda cópia não saiu comprovada: " + r.objRelatorio.ResumoBitPerfect());

                // Faixa a faixa pela ordem (os nomes mudaram ao entrar no acervo; o número não).
                int intIguais = 0;
                for (int i = 0; i < lstAcervo.Count; i++)
                {
                    long n1, n2;
                    string h1 = HashPcm(lstAcervo[i], out n1), h2 = HashPcm(r.lstWma[i], out n2);
                    bool blnIgual = h1 == h2 && n1 == n2;
                    if (blnIgual) intIguais++;
                    Console.WriteLine("{0:D2}  {1}  {2:N0} bytes  {3}  {4}", i + 1, blnIgual ? "IGUAL    " : "DIFERENTE", n1, h1.Substring(0, 16), Path.GetFileName(lstAcervo[i]));
                }
                Assert.AreEqual(lstAcervo.Count, intIguais, "faixas diferentes entre as duas cópias");
            }
            finally { Apoio.Apagar(strPasta); }
        }
    }
}
