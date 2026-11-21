using System;
using System.Collections.Generic;
using System.Windows;
using ARCA.Modelo;

namespace ARCA.Identificar
{
    /// <summary>
    /// As fontes lado a lado, para a pessoa escolher de onde vêm os metadados
    /// do disco: MusicBrainz e GnuDB (achados pelo índice do CD, quando as
    /// faixas o guardam), Apple Music e Discogs (pela busca).
    ///
    /// O que decide se um lançamento é o certo não é o título parecido — é a
    /// estrutura: a mesma quantidade de faixas e as mesmas durações. Por isso
    /// cada faixa mostra a diferença para a duração do arquivo, e o que não
    /// confere vem na cor de alarme.
    ///
    /// Em XAML (editável no VS 2010),
    /// que continua lá. Nada é gravado aqui: a janela só devolve o escolhido.
    /// </summary>
    public partial class JanelaEscolhaFonte : Window
    {
        /// <summary>O candidato escolhido, ou null se a pessoa cancelou.</summary>
        public TCandidatoIdentificacao Escolhido { get; private set; }

        public JanelaEscolhaFonte(TCandidatoIdentificacao objItunes, TCandidatoIdentificacao objDiscogs,
                                  List<int> lstDuracoesDisco, string strBuscado)
            : this(objItunes, objDiscogs, lstDuracoesDisco, null, strBuscado)
        {
        }

        public JanelaEscolhaFonte(TCandidatoIdentificacao objItunes, TCandidatoIdentificacao objDiscogs,
                                  List<int> lstDuracoesDisco, List<string> lstTitulosDisco, string strBuscado)
            : this(new List<KeyValuePair<string, TCandidatoIdentificacao>>
                   {
                       new KeyValuePair<string, TCandidatoIdentificacao>("Apple Music", objItunes),
                       new KeyValuePair<string, TCandidatoIdentificacao>("Discogs", objDiscogs)
                   }, lstDuracoesDisco, lstTitulosDisco, strBuscado)
        {
        }

        /// <param name="lstFontes">Uma coluna por item: o nome da fonte e o que ela achou (null = nada).</param>
        /// <param name="lstTitulosDisco">Títulos das faixas abertas: com faixas avulsas, o par de cada uma no álbum é por título e duração.</param>
        public JanelaEscolhaFonte(List<KeyValuePair<string, TCandidatoIdentificacao>> lstFontes,
                                  List<int> lstDuracoesDisco, List<string> lstTitulosDisco, string strBuscado)
        {
            InitializeComponent();
            List<int> lstDisco = lstDuracoesDisco ?? new List<int>();

            int intTotal = 0;
            foreach (int d in lstDisco) if (d > 0) intTotal += d;
            string strDisco = lstDisco.Count > 0
                ? string.Format("Faixas selecionadas: {0} · {1}", lstDisco.Count, Formato.Tempo(intTotal))
                : "Faixas selecionadas: durações indisponíveis";
            if (!string.IsNullOrEmpty(strBuscado)) strDisco += "   ·   buscado: " + strBuscado;
            m_lblDisco.Text = strDisco;

            m_gradePaineis.Children.Clear();
            foreach (KeyValuePair<string, TCandidatoIdentificacao> kv in lstFontes)
            {
                PainelFonte p = new PainelFonte { Margin = new Thickness(6, 0, 6, 0) };
                p.Mostrar(kv.Key, kv.Value, lstDisco, lstTitulosDisco);
                p.Usar += AoUsar;
                m_gradePaineis.Children.Add(p);
            }
            // Duas colunas cabem nos 1000 px de sempre; com mais, a janela alarga (até a tela).
            if (lstFontes.Count > 2) Width = Math.Min(SystemParameters.WorkArea.Width - 40, 500 * lstFontes.Count);
        }

        private void AoUsar(object sender, EventArgs e)
        {
            Escolhido = ((PainelFonte)sender).objCandidato;
            Close();
        }

        private void AoCancelar(object sender, RoutedEventArgs e)
        {
            Escolhido = null;
            Close();
        }
    }
}
