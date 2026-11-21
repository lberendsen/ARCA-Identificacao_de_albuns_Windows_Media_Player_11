using System.Collections.Generic;
using ARCA.Central;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ARCA.Identificar.Testes
{
    /// <summary>Nomes de arquivo, pasta e álbum que a gravação produz.</summary>
    [TestClass]
    public class TesteNomes
    {
        [TestMethod, TestCategory("Unidade")]
        public void NomeDaFaixa_BarraEDoisPontosViramTraco()
        {
            TFaixaMp3 f = new TFaixaMp3 { strCaminho = @"D:\x\2-10 Perfeição_O Bêbado.MP3", intPosicaoNova = 9, strTituloNovo = "Perfeição/O Bêbado E A equilibrista" };
            Assert.AreEqual("10 Perfeição - O Bêbado E A equilibrista.mp3", GravadorMp3.NomeDaFaixa(f));
        }

        [TestMethod, TestCategory("Unidade")]
        public void NomeDaFaixa_SemNumero()
        {
            TFaixaMp3 f = new TFaixaMp3 { strCaminho = @"D:\x\a.mp3", intPosicaoNova = -1, strTituloNovo = "Get to Denmark" };
            Assert.AreEqual("Get to Denmark.mp3", GravadorMp3.NomeDaFaixa(f));
        }

        [TestMethod, TestCategory("Unidade")]
        public void PastaDoDisco_AnoEAlbum()
        {
            Assert.AreEqual(@"D:\Músicas\MP3\Hélio Delmiro De Souza\2006 - Chill - Brazil 4",
                            SeparadorDesconhecido.PastaDoDisco(@"D:\Músicas\MP3", "Hélio Delmiro De Souza", "2006", "Chill:Brazil 4"));
            Assert.AreEqual(@"D:\Músicas\MP3\Elisa Cristal\Luz de Cristal", SeparadorDesconhecido.PastaDoDisco(@"D:\Músicas\MP3", "Elisa Cristal", "", "Luz de Cristal"));
        }

        [TestMethod, TestCategory("Unidade")]
        public void RaizDoAcervo_DoisNiveisAcima()
        {
            Assert.AreEqual(@"D:\Músicas\MP3", SeparadorDesconhecido.RaizDoAcervo(@"D:\Músicas\MP3\Unknown Artist\Unknown Album\x.mp3"));
        }

        [TestMethod, TestCategory("Unidade")]
        public void NomeDoAlbum_DiscosHomonimosGanhamOAno()
        {
            TDiscografiaDto d = new TDiscografiaDto();
            d.lstDiscos.Add(new TDiscoDto { strTitulo = "Roberto Carlos", intAno = 1966 });
            d.lstDiscos.Add(new TDiscoDto { strTitulo = "Roberto Carlos", intAno = 1969 });
            d.lstDiscos.Add(new TDiscoDto { strTitulo = "O Inimitável", intAno = 1968 });
            Assert.AreEqual("Roberto Carlos (1966)", OrganizadorDiscografia.NomeDoAlbum(d.lstDiscos[0], d));
            Assert.AreEqual("O Inimitável", OrganizadorDiscografia.NomeDoAlbum(d.lstDiscos[2], d));
        }
    }
}
