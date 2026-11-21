using System;
using System.Linq;
using System.Windows;
using ARCA.Identificar;

namespace ARCA.Interop
{
    /// <summary>
    /// No lugar de um ScannerWia local, com a mesma assinatura: o
    /// ImagensDoEncarte, o corte e o "Catalogar à mão" funcionam sem
    /// mudar uma linha. A diferença é de onde vem a imagem: o Vista não tem
    /// driver da G7000, então quem digitaliza é o ARCA Central, no notebook.
    ///
    /// Devolve o caminho de um PNG temporário (quem chama apaga), ou null se a
    /// pessoa cancelou.
    /// </summary>
    public static class ScannerWia
    {
        public static string Digitalizar(out double dblDpi)
        {
            dblDpi = 0;
            JanelaDigitalizar j = new JanelaDigitalizar();
            Window objDono = Application.Current == null ? null
                : Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
            if (objDono != null && objDono != j) j.Owner = objDono;
            j.ShowDialog();
            if (j.strArquivo == null) return null;
            dblDpi = j.dblDpi;
            return j.strArquivo;
        }
    }
}
// A janela (JanelaDigitalizar) está em JanelaDigitalizar.xaml / .xaml.cs.
