using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ARCA.Identificar.Testes
{
    /// <summary>
    /// A conferência no AccurateRip de um álbum real (só leitura): agrupa pelo
    /// índice do CD, decodifica as faixas WMA Lossless, confere o tamanho de cada
    /// uma contra o índice e calcula as assinaturas. Não grava veredito nem ledger.
    /// </summary>
    [TestClass]
    public class TesteAccurateRip
    {
        [TestMethod, TestCategory("Integracao"), Timeout(1200000)]
        public void BuenosHermanos_ConfereComABase()
        {
            Apoio.ExigirCentral();
            string d = Apoio.ACERVO + @"\Ibrahim Ferrer\Buenos Hermanos";
            Apoio.ExigirArquivo(d);
            int intSemIndice;
            var lst = ConferenciaAccurateRip.Agrupar(Directory.GetFiles(d, "*.wma"), out intSemIndice);
            Assert.AreEqual(1, lst.Count, "um disco só");
            Assert.AreEqual(13, lst[0].lstArquivos.Count);
            ConferenciaAccurateRip.Conferir(lst[0], s => { }, () => false);
            Assert.IsNull(lst[0].strProblema, lst[0].strProblema);
            Assert.IsNotNull(lst[0].objResultado);
            Assert.IsTrue(lst[0].objResultado.blnNaBase, "o disco está no AccurateRip");
            Assert.AreEqual(13, lst[0].objResultado.lstFaixas.Count);
            System.Console.WriteLine("conferem: {0} de 13, deslocamento {1}", lst[0].objResultado.QtdConferem, lst[0].objResultado.intOffset);
            Assert.IsTrue(lst[0].objResultado.QtdConferem > 0, "nenhuma faixa conferiu: ripagem ruim ou erro no cálculo");
        }
    }
}
