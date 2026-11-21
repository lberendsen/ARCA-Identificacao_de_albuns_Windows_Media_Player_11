// A G7000 vista pelo notebook: WIA sobre eSCL de rede, sem diálogo.
//
// O Vista não tem driver da G7000, e o diálogo do WIA abriria na tela do
// notebook, onde ninguém está olhando. Então o ARCA Central digitaliza direto:
// área e resolução vêm do pedido, a partir do canto de referência da mesa.
// A imagem volta como PNG (sem perda): a única compressão é a do ARCA
// Identificar, depois do corte.

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ARCA.Central
{
    internal static class ScannerRede
    {
        private const string FORMATO_BMP = "{B96B3CAF-0728-11D3-9D7B-0000F81EF32E}";
        private const int PROP_INTENCAO = 6146, PROP_DPI_H = 6147, PROP_DPI_V = 6148,
                          PROP_INICIO_H = 6149, PROP_INICIO_V = 6150, PROP_LARGURA = 6151, PROP_ALTURA = 6152;
        private const int PROP_MESA_LARGURA = 3074, PROP_MESA_ALTURA = 3075;   // milésimos de polegada
        private const int INTENCAO_COR = 1;

        private static readonly object s_objTrava = new object();

        /// <summary>
        /// Digitaliza dblLarguraCm × dblAlturaCm (cortado à mesa) a intDpi e devolve
        /// o PNG. Uma digitalização por vez: o segundo pedido recebe "ocupado".
        /// </summary>
        public static byte[] Digitalizar(double dblLarguraCm, double dblAlturaCm, int intDpi, Action<string> actLog)
        {
            if (!Monitor.TryEnter(s_objTrava))
                throw new InvalidOperationException("o scanner já está digitalizando outro pedido; espere terminar");
            try
            {
                byte[] arrPng = null; Exception objErro = null;
                // WIA é COM de apartamento único: roda numa thread STA só dela.
                Thread t = new Thread(delegate()
                {
                    try { arrPng = DigitalizarSta(dblLarguraCm, dblAlturaCm, intDpi, actLog); }
                    catch (Exception ex) { objErro = ex; }
                });
                t.SetApartmentState(ApartmentState.STA);
                t.IsBackground = true;
                t.Start();
                if (!t.Join(TimeSpan.FromMinutes(4)))
                    throw new TimeoutException("o scanner não terminou em 4 minutos");
                if (objErro != null) throw objErro;
                return arrPng;
            }
            finally { Monitor.Exit(s_objTrava); }
        }

        private static byte[] DigitalizarSta(double dblLarguraCm, double dblAlturaCm, int intDpi, Action<string> actLog)
        {
            if (intDpi < 75 || intDpi > 1200) intDpi = 300;
            Type objTipo = Type.GetTypeFromProgID("WIA.DeviceManager");
            if (objTipo == null) throw new InvalidOperationException("o WIA não está disponível no notebook");

            dynamic objGerente = Activator.CreateInstance(objTipo);
            dynamic objInfo = null;
            foreach (dynamic di in objGerente.DeviceInfos)
            {
                string strNome = "";
                foreach (dynamic p in di.Properties) if ((string)p.Name == "Name") strNome = Convert.ToString(p.Value);
                if (objInfo == null || strNome.IndexOf("G7000", StringComparison.OrdinalIgnoreCase) >= 0) objInfo = di;
            }
            if (objInfo == null) throw new InvalidOperationException("nenhum scanner encontrado pelo notebook (a G7000 está ligada e na rede?)");

            dynamic objDisp;
            try { objDisp = objInfo.Connect(); }
            catch (COMException ex) { throw new InvalidOperationException("não consegui conectar à G7000: " + Explicar(ex)); }

            double dblMesaL = 8.5, dblMesaA = 11.693;   // polegadas, se o driver não disser
            foreach (dynamic p in objDisp.Properties)
            {
                int id = (int)p.PropertyID;
                if (id == PROP_MESA_LARGURA) dblMesaL = Convert.ToDouble(p.Value) / 1000.0;
                if (id == PROP_MESA_ALTURA) dblMesaA = Convert.ToDouble(p.Value) / 1000.0;
            }
            int intL = (int)Math.Round(Math.Min(dblLarguraCm / 2.54, dblMesaL) * intDpi);
            int intA = (int)Math.Round(Math.Min(dblAlturaCm / 2.54, dblMesaA) * intDpi);

            dynamic objItem = objDisp.Items[1];
            foreach (dynamic p in objItem.Properties)
            {
                switch ((int)p.PropertyID)
                {
                    case PROP_INTENCAO: p.Value = INTENCAO_COR; break;
                    case PROP_DPI_H: p.Value = intDpi; break;
                    case PROP_DPI_V: p.Value = intDpi; break;
                    case PROP_INICIO_H: p.Value = 0; break;
                    case PROP_INICIO_V: p.Value = 0; break;
                }
            }
            // Extensão depois da resolução: o driver limita a extensão pela resolução atual.
            foreach (dynamic p in objItem.Properties)
            {
                int id = (int)p.PropertyID;
                if (id == PROP_LARGURA) p.Value = intL;
                if (id == PROP_ALTURA) p.Value = intA;
            }
            actLog(string.Format("G7000: digitalizando {0} × {1} px a {2} dpi ({3:0.#} × {4:0.#} cm)…",
                intL, intA, intDpi, intL * 2.54 / intDpi, intA * 2.54 / intDpi));

            dynamic objImagem;
            try { objImagem = objItem.Transfer(FORMATO_BMP); }
            catch (COMException ex) { throw new InvalidOperationException("a G7000 falhou: " + Explicar(ex)); }

            string strTemp = Path.Combine(Path.GetTempPath(), "ARCA_central_scan_" + Guid.NewGuid().ToString("N") + ".bmp");
            try
            {
                objImagem.SaveFile(strTemp);
                using (Bitmap bmp = new Bitmap(strTemp))
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.SetResolution(intDpi, intDpi);
                    bmp.Save(ms, ImageFormat.Png);
                    actLog(string.Format("G7000: {0} × {1} px, PNG de {2:N0} bytes.", bmp.Width, bmp.Height, ms.Length));
                    return ms.ToArray();
                }
            }
            finally { try { File.Delete(strTemp); } catch { } }
        }

        private static string Explicar(COMException ex)
        {
            switch (unchecked((uint)ex.ErrorCode))
            {
                case 0x80210015: return "nenhum scanner disponível (desligado ou fora da rede)";
                case 0x80210006: return "o scanner está ocupado";
                case 0x8021000A: return "o scanner não respondeu (desligado?)";
                case 0x80210005: return "o scanner está desligado ou em erro";
                case 0x80210002: return "a tampa está aberta ou há um problema no scanner";
                default: return ex.Message + " (0x" + ex.ErrorCode.ToString("X8") + ")";
            }
        }
    }
}
