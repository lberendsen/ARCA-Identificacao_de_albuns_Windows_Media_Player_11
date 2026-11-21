using System;
using System.Collections.Generic;
using ARCA.Interop;

namespace ARCA.Modelo
{
    /// <summary>O que uma faixa é: música, audiolivro ou fala (entrevista, humor).</summary>
    public enum ConteudoFaixa { Musica = 0, Audiolivro = 1, Fala = 2 }

    /// <summary>
    /// A classe de mídia do ASF: WM/MediaClassPrimaryID e SecondaryID.
    ///
    /// É a marcação que o próprio formato prevê para dizer que um áudio não é
    /// música — melhor que usar o Gênero, que continua livre ("Comédia" num CD
    /// de piada). A classificação é por faixa: as entrevistas do Thriller são
    /// fala dentro de um álbum de música, e continuam com o mesmo Álbum.
    ///
    /// Os GUIDs são os da documentação do Windows Media Format SDK. A Microsoft
    /// dá o stand-up como exemplo de "fala que não é audiolivro".
    /// </summary>
    public static class TClasseMidia
    {
        public const string AtributoPrimario = "WM/MediaClassPrimaryID";
        public const string AtributoSecundario = "WM/MediaClassSecondaryID";

        public static readonly Guid guidPrimariaMusica = new Guid("D1607DBC-E323-4BE2-86A1-48A42A28441E");
        public static readonly Guid guidPrimariaAudioNaoMusica = new Guid("01CD0F29-DA4E-4157-897B-6275D50C4F11");
        public static readonly Guid guidSecundariaAudiolivro = new Guid("E0236BEB-C281-4EDE-A36D-7AF76A3D45B5");
        public static readonly Guid guidSecundariaFala = new Guid("3A172A13-2BD9-4831-835B-114F6A95943F");

        /// <summary>
        /// Grava a classe no arquivo aberto (sem Salvar — quem chama confirma).
        /// Música grava a primária de música e tira a secundária, para que uma
        /// faixa reclassificada não fique com a marca antiga.
        /// </summary>
        public static bool Gravar(EditorAsf objEditor, ConteudoFaixa eConteudo)
        {
            if (objEditor == null) return false;
            if (eConteudo == ConteudoFaixa.Musica)
            {
                objEditor.RemoverAtributo(AtributoSecundario);
                return objEditor.DefinirGuid(AtributoPrimario, guidPrimariaMusica);
            }
            Guid guidSecundaria = eConteudo == ConteudoFaixa.Audiolivro ? guidSecundariaAudiolivro : guidSecundariaFala;
            return objEditor.DefinirGuid(AtributoPrimario, guidPrimariaAudioNaoMusica)
                && objEditor.DefinirGuid(AtributoSecundario, guidSecundaria);
        }

        /// <summary>
        /// A classe lida dos atributos do arquivo. Sem marca, ou com marca que
        /// não é nossa, é música — é o que o acervo inteiro é hoje.
        /// </summary>
        public static ConteudoFaixa Ler(List<AtributoAsf> lstAtributos)
        {
            Guid guidPrimaria = Guid.Empty, guidSecundaria = Guid.Empty;
            if (lstAtributos != null)
            {
                foreach (AtributoAsf objAtributo in lstAtributos)
                {
                    if (objAtributo.Tipo != TipoAtributoAsf.Guid || string.IsNullOrEmpty(objAtributo.Valor)) continue;
                    Guid guidValor;
                    try { guidValor = new Guid(objAtributo.Valor); }
                    catch { continue; }
                    if (string.Equals(objAtributo.Nome, AtributoPrimario, StringComparison.OrdinalIgnoreCase)) guidPrimaria = guidValor;
                    else if (string.Equals(objAtributo.Nome, AtributoSecundario, StringComparison.OrdinalIgnoreCase)) guidSecundaria = guidValor;
                }
            }
            if (guidPrimaria != guidPrimariaAudioNaoMusica) return ConteudoFaixa.Musica;
            if (guidSecundaria == guidSecundariaAudiolivro) return ConteudoFaixa.Audiolivro;
            return ConteudoFaixa.Fala;
        }
    }
}
