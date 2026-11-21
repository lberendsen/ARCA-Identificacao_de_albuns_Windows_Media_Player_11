using System.Collections.Generic;
using ARCA.Central;
using ARCA.Modelo;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ARCA.Identificar.Testes
{
    /// <summary>Casamento das faixas com o que as fontes acham: títulos, pareamento de faixas avulsas, faixas soltas do Álbum Desconhecido.</summary>
    [TestClass]
    public class TesteCasamento
    {
        private static TFaixaSolta Solta(string strCaminho, string strTitulo = "", string strArtista = "", string strAlbum = "", double dblDur = 200)
        {
            TFaixaSolta l = new TFaixaSolta
            {
                objFaixa = new TFaixaMp3 { strCaminho = strCaminho, strTitulo = strTitulo, strArtista = strArtista, strArtistaAlbum = "", strAlbum = strAlbum, strGenero = "", dblDuracao = dblDur }
            };
            CasadorFaixasSoltas.Preparar(l);
            return l;
        }

        private const string UNK = @"D:\Músicas\MP3\Unknown Artist\Unknown Album\";

        [TestMethod, TestCategory("Unidade")]
        public void Preparar_ArtistaTitulo_ECopiaNumerada()
        {
            TFaixaSolta l = Solta(UNK + "IVETE SANGALO-QUANDO A CHUVA PASSAR 1.mp3");
            Assert.AreEqual("IVETE SANGALO", l.strArtistaBusca);
            Assert.AreEqual("QUANDO A CHUVA PASSAR", l.strTituloBusca);
        }

        [TestMethod, TestCategory("Unidade")]
        public void Preparar_VariousArtists_FicaSoOTitulo()
        {
            TFaixaSolta l = Solta(UNK + "Various Artists-Di Tale Amor Che Dir.mp3");
            Assert.AreEqual("", l.strArtistaBusca);
            Assert.AreEqual("Di Tale Amor Che Dir", l.strTituloBusca);
        }

        [TestMethod, TestCategory("Unidade")]
        public void Preparar_HifenDentroDeParentese_NaoSepara()
        {
            Assert.AreEqual("Romeo und Julia (Phantasie-Ouvert", Solta(UNK + "05 Romeo und Julia (Phantasie-Ouvert.mp3").strTituloBusca);
        }

        [TestMethod, TestCategory("Unidade")]
        public void Preparar_NomeSemEspaco_HifensViramEspaco()
        {
            TFaixaSolta l = Solta(UNK + "aspecto-sutil.mp3");
            Assert.AreEqual("aspecto sutil", l.strTituloBusca);
            Assert.AreEqual("", l.strArtistaBusca);
        }

        [TestMethod, TestCategory("Unidade")]
        public void Preparar_FaixaN_NaoETitulo()
        {
            Assert.AreEqual("", Solta(UNK + "01 01 Faixa Desconhecida 1.mp3", "01 Faixa Desconhecida 1").strTituloBusca);
        }

        [TestMethod, TestCategory("Unidade")]
        public void Preparar_CdRipadoSemInformacao_ArtistaVemDaPastaDeCima()
        {
            TFaixaSolta l = Solta(@"D:\Músicas\MP3\George Michael(grupo)\Álbum Desconhecido (1_3_2006 15_53_37)\05 Dangers Us.mp3");
            Assert.AreEqual("George Michael", l.strArtistaBusca);
        }

        [TestMethod, TestCategory("Unidade")]
        public void SimilaridadeTitulo_TituloTruncado()
        {
            Assert.IsTrue(CasadorFaixasSoltas.SimilaridadeTitulo("The Magic Flute, K", "The Magic Flute, K. 620: Overture") >= 0.8);
        }

        [TestMethod, TestCategory("Unidade")]
        public void Similaridade_TituloComTraducaoDoDiscogs()
        {
            Assert.IsTrue(CasadorDiscografia.Similaridade("Esqueça", OrganizadorDiscografia.SemTraducao("Esqueça = Forget Him")) >= 0.99);
        }

        [TestMethod, TestCategory("Unidade")]
        public void Confianca_SemArtista_SoEAltaComTudoEmCheio()
        {
            TFaixaSolta l = Solta(UNK + "Africa.mp3");
            Assert.AreEqual("alta", CasadorFaixasSoltas.Confianca(l, new TOpcaoApple { dblSimTitulo = 1, intDif = 1, dblSimArtista = -1 }));
            Assert.AreEqual("média", CasadorFaixasSoltas.Confianca(l, new TOpcaoApple { dblSimTitulo = 1, intDif = 3, dblSimArtista = -1 }));
            Assert.AreEqual("baixa", CasadorFaixasSoltas.Confianca(l, new TOpcaoApple { dblSimTitulo = 0.9, intDif = 9, dblSimArtista = -1 }));
        }

        private static TCandidatoIdentificacao Album12()
        {
            TCandidatoIdentificacao c = new TCandidatoIdentificacao { strFonte = "Apple Music", strArtista = "Stanley Clarke", strAlbum = "1, 2, to the Bass" };
            string[] t = { "1, 2, to the Bass", "Simply Said", "Where Is the Love (featuring Glenn Lewis & Amel Larrieux)", "Anna", "Los Caballos", "Just Cruizin'",
                           "'Bout the Bass", "Hair", "Touch", "Todos los Niños", "I Shall Not Be Moved", "Shanti" };
            int[] d = { 355, 275, 246, 302, 281, 266, 452, 314, 363, 297, 398, 354 };
            for (int i = 0; i < 12; i++) { c.lstTitulos.Add(t[i]); c.lstArtistasFaixas.Add(""); c.lstDuracoes.Add(d[i]); }
            return c;
        }

        [TestMethod, TestCategory("Unidade")]
        public void Parear_FaixaAvulsa_CaiNaPosicaoCerta()
        {
            // O caso do Stanley Clarke: só a faixa 3 aberta; antes ela era comparada com a 1.
            int[] vet = PainelFonte.Parear(Album12(), new List<int> { 246 }, new List<string> { "Where Is The Love (featuring Glenn Lewis & Amel Larrieux)" });
            Assert.AreEqual(0, vet[2]);
            for (int j = 0; j < 12; j++) if (j != 2) Assert.AreEqual(-1, vet[j]);
        }

        [TestMethod, TestCategory("Unidade")]
        public void Parear_DiscoInteiro_PelaPosicao()
        {
            List<int> lstDur = new List<int>();
            for (int i = 0; i < 12; i++) lstDur.Add(100);   // durações nada a ver: com o disco inteiro, vale a posição
            int[] vet = PainelFonte.Parear(Album12(), lstDur, null);
            for (int j = 0; j < 12; j++) Assert.AreEqual(j, vet[j]);
        }
    }
}
