using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ARCA.Interop;
using ARCA.Modelo;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ARCA.Identificar.Testes
{
    /// <summary>
    /// O leitor de CD do Vista, de verdade, sem privilégio de administrador (é como o WMP
    /// abre o ARCA Ripar). Precisa de um CD de áudio no leitor; a letra vem de
    /// ARCA_TESTE_LEITOR (padrão E). Sem disco, os testes ficam inconclusivos.
    /// Nada é gravado: só leitura do disco.
    /// </summary>
    [TestClass]
    public class TesteLeitorCd
    {
        private static string Letra
        {
            get { string s = Environment.GetEnvironmentVariable("ARCA_TESTE_LEITOR"); return string.IsNullOrEmpty(s) ? "E" : s.TrimEnd(':', '\\'); }
        }

        private static SptiDiscReader Abrir(out string strPasta)
        {
            strPasta = Apoio.PastaNova();
            SptiDiscReader r = new SptiDiscReader(new AcquisitionLogger(strPasta, null));
            if (!r.AbrirUnidade(Letra)) { r.Dispose(); Apoio.Apagar(strPasta); Assert.Inconclusive("não abri o leitor " + Letra + ":"); }
            CdromTocReader.CDROM_TOC t;
            if (!r.LerToc(out t)) { r.Dispose(); Apoio.Apagar(strPasta); Assert.Inconclusive("sem CD de áudio no leitor " + Letra + ":"); }
            return r;
        }

        /// <summary>Os LBAs do índice comum (faixas e lead-out).</summary>
        private static List<int> Lbas(CdromTocReader.CDROM_TOC t)
        {
            List<int> lst = new List<int>();
            for (int i = 0; i <= t.LastTrack - t.FirstTrack + 1; i++)
            {
                CdromTocReader.TRACK_DATA d = t.TrackData[i];
                lst.Add((d.Address_1 * 60 + d.Address_2) * 75 + d.Address_3 - 150);
            }
            return lst;
        }

        [TestMethod, TestCategory("Integracao")]
        public void Leitor_IdentificaOModeloEOOffsetConhecido()
        {
            string strPasta;
            using (SptiDiscReader r = Abrir(out strPasta))
            try
            {
                string strFab, strMod, strRev;
                Assert.IsTrue(r.ObterInquiryHardware(out strFab, out strMod, out strRev), "INQUIRY falhou");
                Console.WriteLine("leitor: [{0}] [{1}] [{2}]", strFab, strMod, strRev);
                Assert.IsFalse(string.IsNullOrEmpty(strMod), "o leitor não disse o modelo");
                // Como o Programa.Main faz: o Vista não sai para a internet, a tabela vem pelo Central.
                Apoio.ExigirCentral();
                OffsetDrive.objObterTabela = ClienteCentral.TabelaDeOffsets;
                string strOrigem;
                int? n = OffsetDrive.Obter(strFab, strMod, out strOrigem);
                Console.WriteLine("offset: {0} ({1})", n, strOrigem);
                Assert.IsTrue(n.HasValue, "o offset do leitor não se sabe — a tabela veio do ARCA Central?");
                if (strMod.Trim().StartsWith("DRW-24F1MT")) Assert.AreEqual(6, n.Value, "o ASUS DRW-24F1MT lê +6 amostras adiantado");
            }
            finally { r.Dispose(); Apoio.Apagar(strPasta); }   // fechar antes: o log do leitor fica aberto até lá
        }

        [TestMethod, TestCategory("Integracao")]
        public void Indice_ComOSessoesECdText()
        {
            string strPasta;
            using (SptiDiscReader r = Abrir(out strPasta))
            try
            {
                CdromTocReader.CDROM_TOC t;
                Assert.IsTrue(r.LerToc(out t));
                List<int> lst = Lbas(t);
                Console.WriteLine("faixas {0}..{1}; LBAs {2}", t.FirstTrack, t.LastTrack, string.Join(" ", lst.Select(x => x.ToString()).ToArray()));
                Assert.IsTrue(t.LastTrack >= t.FirstTrack);
                for (int i = 1; i < lst.Count; i++) Assert.IsTrue(lst[i] > lst[i - 1], "o índice não está em ordem");

                List<SptiDiscReader.TSessao> lstSes = r.LerSessoes();
                if (lstSes != null)
                    Assert.AreEqual(lst[0], lstSes[0].lstFaixas[0][1], "o índice completo e o comum discordam da faixa 1");

                string alb, art; Dictionary<int, string> dicT, dicA;
                bool blnTexto = r.LerCdText(out alb, out art, out dicT, out dicA);
                Console.WriteLine("CD-TEXT: {0}; álbum [{1}], intérprete [{2}], {3} título(s)", blnTexto, alb, art, dicT == null ? 0 : dicT.Count);
            }
            finally { r.Dispose(); Apoio.Apagar(strPasta); }   // fechar antes: o log do leitor fica aberto até lá
        }

        /// <summary>
        /// A base do bit-perfect sem AccurateRip: o mesmo trecho lido duas vezes é idêntico.
        /// 3 trechos de 75 setores (1 s cada): começo, meio e fim do disco.
        /// </summary>
        [TestMethod, TestCategory("Integracao")]
        public void DuasLeituras_DoMesmoTrecho_SaoIdenticas()
        {
            string strPasta;
            using (SptiDiscReader r = Abrir(out strPasta))
            try
            {
                CdromTocReader.CDROM_TOC t;
                Assert.IsTrue(r.LerToc(out t));
                List<int> lst = Lbas(t);
                int intFim = lst[lst.Count - 1];
                const uint N = 25;   // setores por pedido (≈ 58 KB, abaixo do limite de transferência)
                foreach (int intInicio in new[] { lst[0] + 150, (lst[0] + intFim) / 2, intFim - 300 })
                {
                    byte[] a1 = Ler(r, (uint)intInicio, 3 * N), a2 = Ler(r, (uint)intInicio, 3 * N);
                    Assert.IsTrue(a1.Any(b => b != 0) || intInicio > intFim - 400, "o trecho no LBA " + intInicio + " veio todo em silêncio");
                    if (!a1.SequenceEqual(a2))
                    {
                        // Instrumentação: quanto difere, onde, se é deslocamento (jitter) e qual leitura a terceira confirma.
                        byte[] a3 = Ler(r, (uint)intInicio, 3 * N);
                        string strD = Diferenca(a1, a2);
                        Console.WriteLine("LBA {0}: 1ª × 2ª: {1}", intInicio, strD);
                        Console.WriteLine("LBA {0}: 3ª leitura {1}", intInicio, a3.SequenceEqual(a1) ? "= 1ª" : a3.SequenceEqual(a2) ? "= 2ª" : "difere das duas: " + Diferenca(a1, a3));
                        Assert.Fail("duas leituras do LBA " + intInicio + " diferem: " + strD);
                    }
                    Console.WriteLine("LBA {0}: {1} bytes, duas leituras idênticas", intInicio, a1.Length);
                }
            }
            finally { r.Dispose(); Apoio.Apagar(strPasta); }   // fechar antes: o log do leitor fica aberto até lá
        }

        /// <summary>Quantos bytes diferem, em que setores, e se a segunda é a primeira deslocada (jitter do leitor).</summary>
        private static string Diferenca(byte[] a, byte[] b)
        {
            int intPrim = -1, intUlt = -1, intN = 0;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) { if (intPrim < 0) intPrim = i; intUlt = i; intN++; }
            if (intN == 0) return "iguais";
            string strDesloc = "";
            int intJanela = Math.Min(4096, a.Length - intPrim - 2400 * 4);
            for (int k = -2352 * 2; k <= 2352 * 2 && intJanela > 0; k += 4)
            {
                if (k == 0 || intPrim + k < 0 || intPrim + k + intJanela > b.Length) continue;
                bool blnIgual = true;
                for (int i = 0; i < intJanela && blnIgual; i++) if (a[intPrim + i] != b[intPrim + k + i]) blnIgual = false;
                if (blnIgual) { strDesloc = string.Format("; a 2ª é a 1ª deslocada {0:+0;-0} bytes ({1:+0;-0} amostras) a partir dali", k, k / 4); break; }
            }
            return string.Format("{0:N0} byte(s) diferentes, do byte {1:N0} (setor {2}, +{3}) ao {4:N0} (setor {5}){6}",
                                 intN, intPrim, intPrim / 2352, intPrim % 2352, intUlt, intUlt / 2352, strDesloc);
        }

        private static byte[] Ler(SptiDiscReader r, uint lba, uint n)
        {
            const uint N = 25;
            byte[] arr = new byte[n * 2352];
            byte[] buf = new byte[N * 2352];
            GCHandle h = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                for (uint k = 0; k < n; k += N)
                {
                    uint dw; byte st, sk, asc, ascq; int err;
                    Assert.IsTrue(r.LerBlocoSpti(h.AddrOfPinnedObject(), lba + k, N, true, out dw, out st, out sk, out asc, out ascq, out err),
                                  string.Format("READ CD no LBA {0} falhou: status {1}, sense {2:X}/{3:X}/{4:X}, erro {5}", lba + k, st, sk, asc, ascq, err));
                    Array.Copy(buf, 0, arr, k * 2352, N * 2352);
                }
            }
            finally { h.Free(); }
            return arr;
        }
    }
}
