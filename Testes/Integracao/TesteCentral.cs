using System.Collections.Generic;
using System.IO;
using System.Linq;
using ARCA.Central;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ARCA.Identificar.Testes
{
    /// <summary>As buscas pelo ARCA Central (notebook): nome, link, índice do CD, som, faixa, discografia, AccurateRip.</summary>
    [TestClass]
    public class TesteCentral
    {
        [TestInitialize] public void Antes() { Apoio.ExigirCentral(); }

        [TestMethod, TestCategory("Integracao"), Timeout(120000)]
        public void Buscar_PorArtistaEAlbum()
        {
            TResposta r = ClienteCentral.Buscar("Ibrahim Ferrer", "Buenos Hermanos", 13);
            Assert.IsTrue(r.objApple != null || r.objDiscogs != null, r.strErro);
            TResposta d = r.objDiscogs != null ? r : null;
            if (d != null) Assert.AreEqual(13, d.objDiscogs.lstTitulos.Count);
        }

        [TestMethod, TestCategory("Integracao"), Timeout(120000)]
        public void PorToc_MusicBrainzAchaODiscoExato()
        {
            TResposta r = ClienteCentral.PorToc(TesteIndiceDoCd.MCDI_BUENOS_HERMANOS);
            TCandidatoDto mb = r.lstCandidatos.FirstOrDefault(c => c.strFonte == "MusicBrainz");
            Assert.IsNotNull(mb, r.strErro);
            Assert.AreEqual("Ibrahim Ferrer", mb.strArtista);
            StringAssert.Contains(mb.strAlbum.ToLowerInvariant(), "buenos hermanos");
            Assert.AreEqual(13, mb.lstTitulos.Count);
            StringAssert.Contains(mb.strDetalhe, "índice do CD confere");
        }

        [TestMethod, TestCategory("Integracao"), Timeout(300000)]
        public void PorSom_TitulosTraduzidos_AchaOAlbum()
        {
            // O "18" do Moby em WMA, com os títulos em português: pelo nome não acha; pelo som, sim.
            string d = Apoio.ACERVO + @"\Moby\18";
            Apoio.ExigirArquivo(d);
            List<string> lst = Directory.GetFiles(d, "*.wma").OrderBy(s => s).Take(3).ToList();
            TResposta r = ClienteCentral.PorSom(lst);
            Assert.IsTrue(r.lstCandidatos.Count > 0, r.strErro);
            Assert.AreEqual("Moby", r.lstCandidatos[0].strArtista);
            Assert.AreEqual("18", r.lstCandidatos[0].strAlbum);
        }

        [TestMethod, TestCategory("Integracao"), Timeout(120000)]
        public void Faixa_AchaPeloTitulo()
        {
            TBuscaFaixaDto b = ClienteCentral.Faixa("", "Eduardo e Monica");
            Assert.IsTrue(b.lstFaixas.Any(f => f.strArtista.Contains("Legião Urbana")), b.strErro);
        }

        [TestMethod, TestCategory("Integracao"), Timeout(120000)]
        public void PorLink_IdDoAlbumNoAppleMusic()
        {
            TResposta r = ClienteCentral.PorLink("190892332", 12);   // "1, 2, to the Bass", Stanley Clarke
            Assert.IsNotNull(r.objCandidato, r.strErro);
            Assert.AreEqual(12, r.objCandidato.lstTitulos.Count);
        }

        [TestMethod, TestCategory("Integracao"), Timeout(120000)]
        public void AccurateRip_ArquivoDaBase()
        {
            TResposta r = ClienteCentral.AccurateRip(TesteIndiceDoCd.MCDI_BUENOS_HERMANOS, 13);
            Assert.IsNull(r.strErro);
            Assert.IsFalse(r.blnForaDaBase, "o disco deveria estar na base");
            Assert.IsTrue(r.strDadosBase64.Length > 100);
        }

        [TestMethod, TestCategory("Integracao"), Timeout(600000)]
        public void Discografia_PronTaDoCache()
        {
            // A do Roberto Carlos já foi lida (cache no notebook): volta pronta na hora.
            TDiscografiaDto d = ClienteCentral.Discografia("Roberto Carlos");
            for (int i = 0; i < 200 && !d.blnPronta && d.strErro == null; i++) { System.Threading.Thread.Sleep(3000); d = ClienteCentral.Discografia("Roberto Carlos"); }
            Assert.IsTrue(d.blnPronta, d.strErro);
            Assert.IsTrue(d.lstDiscos.Count > 20);
        }
    }
}
