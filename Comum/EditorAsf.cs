using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ARCA.Interop
{
    public enum TipoAtributoAsf
    {
        Dword = 0, Texto = 1, Binario = 2, Booleano = 3,
        Qword = 4, Word = 5, Guid = 6
    }

    public sealed class AtributoAsf
    {
        public string Nome { get; set; }
        public TipoAtributoAsf Tipo { get; set; }
        public string Valor { get; set; }
        public int Bytes { get; set; }
        public ushort Indice { get; set; }
        public ushort Idioma { get; set; }

        public bool Binario { get { return Tipo == TipoAtributoAsf.Binario; } }

        public string TipoTexto
        {
            get
            {
                switch (Tipo)
                {
                    case TipoAtributoAsf.Texto: return "texto";
                    case TipoAtributoAsf.Dword: return "número (32)";
                    case TipoAtributoAsf.Qword: return "número (64)";
                    case TipoAtributoAsf.Word: return "número (16)";
                    case TipoAtributoAsf.Booleano: return "sim/não";
                    case TipoAtributoAsf.Guid: return "GUID";
                    case TipoAtributoAsf.Binario: return "binário";
                    default: return "?";
                }
            }
        }
    }

    /// <summary>
    /// Leitura e escrita de atributos no contêiner ASF, pelo Windows Media
    /// Format SDK (`wmvcore.dll`).
    ///
    /// As declarações
    /// certas — o que foi acrescentado aqui é o lado da **leitura**: enumerar
    /// tudo que existe no arquivo, inclusive os atributos customizados, que é o
    /// que o `getItemInfo` do player não alcança.
    ///
    /// Por que não usar a biblioteca do WMP para isso: ela devolve o que o
    /// banco dela guardou, não o que está no arquivo. Para um acervo, a
    /// diferença importa.
    /// </summary>
    public sealed class EditorAsf : IDisposable
    {
        private const ushort NivelArquivo = 0;

        [DllImport("wmvcore.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int WMCreateEditor(out IWMMetadataEditor editor);

        [ComImport, Guid("96406BD9-2B2B-11D3-B36B-00C04F6108FF"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWMMetadataEditor
        {
            void Open([In, MarshalAs(UnmanagedType.LPWStr)] string arquivo);
            void Close();
            void Flush();
        }

        [ComImport, Guid("15CC68E3-27CC-4ECD-B222-3F5D02D80BD5"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWMHeaderInfo3
        {
            void GetAttributeCount(ushort stream, out ushort qtd);
            // wmsdkidl.h: GetAttributeByIndex(WORD wIndex, WORD* pwStreamNum, ...).
            // A declaração anterior trocava índice e stream (não era usada).
            void GetAttributeByIndex(ushort indice, ref ushort stream,
                [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder nome, ref ushort nomeLen,
                out TipoAtributoAsf tipo, IntPtr valor, ref ushort valorLen);
            void GetAttributeByName(ref ushort stream,
                [In, MarshalAs(UnmanagedType.LPWStr)] string nome,
                out TipoAtributoAsf tipo, IntPtr valor, ref ushort valorLen);
            void SetAttribute(ushort stream, [In, MarshalAs(UnmanagedType.LPWStr)] string nome,
                TipoAtributoAsf tipo, IntPtr valor, ushort tamanho);
            void GetMarkerCount(out ushort qtd);
            void GetMarker(ushort indice,
                [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder nome, ref ushort nomeLen,
                out ulong tempo);
            void AddMarker([In, MarshalAs(UnmanagedType.LPWStr)] string nome, ulong tempo);
            void RemoveMarker(ushort indice);
            void GetScriptCount(out ushort qtd);
            void GetScript(ushort indice,
                [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder tipo, ref ushort tipoLen,
                [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder comando, ref ushort comandoLen,
                out ulong tempo);
            void AddScript([In, MarshalAs(UnmanagedType.LPWStr)] string tipo,
                [In, MarshalAs(UnmanagedType.LPWStr)] string comando, ulong tempo);
            void RemoveScript(ushort indice);
            void GetCodecInfoCount(out uint qtd);
            void GetCodecInfo(uint indice, ref ushort nomeLen,
                [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder nome, ref ushort descLen,
                [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder desc,
                out TipoAtributoAsf tipo, ref ushort infoLen, IntPtr info);
            void GetAttributeCountEx(ushort stream, out ushort qtd);
            void GetAttributeIndices(ushort stream,
                [In, MarshalAs(UnmanagedType.LPWStr)] string nome,
                IntPtr idioma, IntPtr indices, ref ushort qtdIndices);
            void GetAttributeByIndexEx(ushort stream, ushort indice,
                [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder nome, ref ushort nomeLen,
                out TipoAtributoAsf tipo, out ushort idioma, IntPtr valor, ref uint valorLen);
            void ModifyAttribute(ushort stream, ushort indice, TipoAtributoAsf tipo,
                ushort idioma, IntPtr valor, uint tamanho);
            void AddAttribute(ushort stream, [In, MarshalAs(UnmanagedType.LPWStr)] string nome,
                out ushort indice, TipoAtributoAsf tipo, ushort idioma, IntPtr valor, uint tamanho);
            void DeleteAttribute(ushort stream, ushort indice);
        }

        private IWMMetadataEditor _editor;
        private IWMHeaderInfo3 _cabecalho;

        public string Falha { get; private set; }
        public bool Aberto { get { return _cabecalho != null; } }

        public bool Abrir(string arquivo)
        {
            Fechar();
            Falha = null;
            try
            {
                int hr = WMCreateEditor(out _editor);
                if (hr < 0 || _editor == null)
                {
                    Falha = "WMCreateEditor devolveu 0x" + hr.ToString("X8");
                    return false;
                }
                _editor.Open(arquivo);
                _cabecalho = _editor as IWMHeaderInfo3;
                if (_cabecalho == null)
                {
                    Falha = "O arquivo abriu, mas não expôs IWMHeaderInfo3.";
                    Fechar();
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Falha = ex.GetType().Name + ": " + ex.Message;
                Fechar();
                return false;
            }
        }

        /// <summary>Todos os atributos de nível de arquivo, como estão no ASF.</summary>
        public List<AtributoAsf> LerTudo()
        {
            List<AtributoAsf> lista = new List<AtributoAsf>();
            if (_cabecalho == null) return lista;

            ushort qtd;
            try { _cabecalho.GetAttributeCountEx(NivelArquivo, out qtd); }
            catch (Exception ex) { Falha = ex.Message; return lista; }

            for (ushort i = 0; i < qtd; i++)
            {
                IntPtr buffer = IntPtr.Zero;
                try
                {
                    // Primeira chamada com ponteiros nulos: o SDK devolve os
                    // tamanhos necessários. Só então se aloca.
                    ushort nomeLen = 0;
                    uint valorLen = 0;
                    TipoAtributoAsf tipo;
                    ushort idioma;
                    _cabecalho.GetAttributeByIndexEx(NivelArquivo, i, null, ref nomeLen,
                                                     out tipo, out idioma, IntPtr.Zero, ref valorLen);

                    StringBuilder nome = new StringBuilder(nomeLen + 1);
                    buffer = valorLen > 0 ? Marshal.AllocHGlobal((int)valorLen) : IntPtr.Zero;

                    _cabecalho.GetAttributeByIndexEx(NivelArquivo, i, nome, ref nomeLen,
                                                     out tipo, out idioma, buffer, ref valorLen);

                    lista.Add(new AtributoAsf
                    {
                        Nome = nome.ToString(),
                        Tipo = tipo,
                        Idioma = idioma,
                        Indice = i,
                        Bytes = (int)valorLen,
                        Valor = Interpretar(tipo, buffer, valorLen)
                    });
                }
                catch (Exception ex)
                {
                    lista.Add(new AtributoAsf
                    {
                        Nome = "(atributo " + i + ")",
                        Tipo = TipoAtributoAsf.Texto,
                        Indice = i,
                        Valor = "não consegui ler — " + ex.Message
                    });
                }
                finally
                {
                    if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                }
            }
            return lista;
        }

        private static string Interpretar(TipoAtributoAsf tipo, IntPtr p, uint tamanho)
        {
            if (p == IntPtr.Zero || tamanho == 0) return "";
            switch (tipo)
            {
                case TipoAtributoAsf.Texto:
                    // Pelo tamanho declarado, não até achar um zero: texto sem
                    // terminador fazia ler memória além do buffer.
                    return (Marshal.PtrToStringUni(p, (int)(tamanho / 2)) ?? "").TrimEnd('\0');
                case TipoAtributoAsf.Dword:
                    return tamanho >= 4 ? ((uint)Marshal.ReadInt32(p)).ToString() : "";
                case TipoAtributoAsf.Qword:
                    return tamanho >= 8 ? Marshal.ReadInt64(p).ToString() : "";
                case TipoAtributoAsf.Word:
                    return tamanho >= 2 ? ((ushort)Marshal.ReadInt16(p)).ToString() : "";
                case TipoAtributoAsf.Booleano:
                    return tamanho >= 4 ? (Marshal.ReadInt32(p) != 0 ? "sim" : "não") : "";
                case TipoAtributoAsf.Guid:
                    if (tamanho < 16) return "";
                    byte[] g = new byte[16];
                    Marshal.Copy(p, g, 0, 16);
                    return new Guid(g).ToString();
                case TipoAtributoAsf.Binario:
                    return "[" + tamanho.ToString("N0") + " bytes]";
                default:
                    return "";
            }
        }

        /// <summary>
        /// Extrai a imagem embutida em WM/Picture.
        ///
        /// O valor do atributo é uma estrutura WM_PICTURE cujos campos são
        /// ponteiros para dentro do próprio buffer. Duas complicações:
        ///
        /// 1. Os bytes só valem enquanto o buffer existe, então a cópia tem de
        ///    ser feita aqui dentro — não dá para devolver o valor bruto e
        ///    parsear depois.
        /// 2. O alinhamento da estrutura é ambíguo: o cabeçalho C daria
        ///    deslocamentos 0/4/8/12/16 por alinhamento natural, mas o código
        ///    antigo a declarava com Pack=1, o que daria 0/4/5/9/13.
        ///    Em vez de apostar, testo os dois e aceito o que produzir um
        ///    resultado coerente — ponteiro dentro do buffer e tamanho plausível.
        /// </summary>
        /// <summary>
        /// O conteúdo bruto de um atributo de nível de arquivo, ou null se não
        /// houver. Serve ao WM/MCDI, o índice do CD de origem que o WMP grava
        /// como bytes (texto UTF-16 por dentro).
        /// </summary>
        public byte[] LerBruto(string strNome)
        {
            if (_cabecalho == null) return null;
            ushort qtd;
            try { _cabecalho.GetAttributeCountEx(NivelArquivo, out qtd); }
            catch { return null; }

            for (ushort i = 0; i < qtd; i++)
            {
                IntPtr buffer = IntPtr.Zero;
                try
                {
                    ushort nomeLen = 0;
                    uint valorLen = 0;
                    TipoAtributoAsf tipo;
                    ushort idioma;
                    _cabecalho.GetAttributeByIndexEx(NivelArquivo, i, null, ref nomeLen,
                                                     out tipo, out idioma, IntPtr.Zero, ref valorLen);
                    StringBuilder nome = new StringBuilder(nomeLen + 1);
                    buffer = Marshal.AllocHGlobal((int)Math.Max(1, valorLen));
                    _cabecalho.GetAttributeByIndexEx(NivelArquivo, i, nome, ref nomeLen,
                                                     out tipo, out idioma, buffer, ref valorLen);
                    if (!string.Equals(nome.ToString(), strNome, StringComparison.OrdinalIgnoreCase)) continue;
                    byte[] arr = new byte[valorLen];
                    Marshal.Copy(buffer, arr, 0, (int)valorLen);
                    return arr;
                }
                catch { }
                finally
                {
                    if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                }
            }
            return null;
        }

        /// <summary>O WM/MCDI como texto ("A+96+5C1C+…"), ou null.</summary>
        public string LerMcdi()
        {
            byte[] arr = LerBruto("WM/MCDI");
            if (arr == null || arr.Length < 2) return null;
            return Encoding.Unicode.GetString(arr).TrimEnd('\0');
        }

        public byte[] LerImagem(out string mime, out string descricao)
        {
            mime = null;
            descricao = null;
            if (_cabecalho == null) return null;

            ushort qtd;
            try { _cabecalho.GetAttributeCountEx(NivelArquivo, out qtd); }
            catch { return null; }

            for (ushort i = 0; i < qtd; i++)
            {
                IntPtr buffer = IntPtr.Zero;
                try
                {
                    ushort nomeLen = 0;
                    uint valorLen = 0;
                    TipoAtributoAsf tipo;
                    ushort idioma;
                    _cabecalho.GetAttributeByIndexEx(NivelArquivo, i, null, ref nomeLen,
                                                     out tipo, out idioma, IntPtr.Zero, ref valorLen);
                    if (tipo != TipoAtributoAsf.Binario || valorLen < 17) continue;

                    StringBuilder nome = new StringBuilder(nomeLen + 1);
                    buffer = Marshal.AllocHGlobal((int)valorLen);
                    _cabecalho.GetAttributeByIndexEx(NivelArquivo, i, nome, ref nomeLen,
                                                     out tipo, out idioma, buffer, ref valorLen);

                    if (!string.Equals(nome.ToString(), "WM/Picture", StringComparison.OrdinalIgnoreCase))
                        continue;

                    byte[] imagem = TentarLayout(buffer, valorLen, 8, 12, 16, out mime, out descricao)
                                 ?? TentarLayout(buffer, valorLen, 5, 9, 13, out mime, out descricao);
                    if (imagem != null) return imagem;
                }
                catch { }
                finally
                {
                    if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                }
            }
            return null;
        }

        private static byte[] TentarLayout(IntPtr buffer, uint total,
                                           int offDescricao, int offTamanho, int offDados,
                                           out string mime, out string descricao)
        {
            mime = null;
            descricao = null;
            try
            {
                IntPtr pMime = Marshal.ReadIntPtr(buffer, 0);
                IntPtr pDesc = Marshal.ReadIntPtr(buffer, offDescricao);
                uint tamanho = (uint)Marshal.ReadInt32(buffer, offTamanho);
                IntPtr pDados = Marshal.ReadIntPtr(buffer, offDados);

                if (pDados == IntPtr.Zero || tamanho == 0 || tamanho > 64 * 1024 * 1024)
                    return null;

                // Coerência: o ponteiro dos dados tem de cair dentro do buffer,
                // e o que ele aponta tem de caber no que sobra.
                long inicio = buffer.ToInt64();
                long dados = pDados.ToInt64();
                if (dados < inicio || dados + tamanho > inicio + total) return null;

                byte[] bytes = new byte[tamanho];
                Marshal.Copy(pDados, bytes, 0, (int)tamanho);

                // Selo de arquivo de imagem: se não for JPEG nem PNG, o
                // deslocamento provavelmente está errado.
                bool jpeg = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8;
                bool png = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50;
                if (!jpeg && !png) return null;

                if (pMime != IntPtr.Zero) mime = Marshal.PtrToStringUni(pMime);
                if (pDesc != IntPtr.Zero) descricao = Marshal.PtrToStringUni(pDesc);
                return bytes;
            }
            catch { return null; }
        }

        /// <summary>Grava (ou cria) um atributo de texto no nível do arquivo.</summary>
        /// <summary>
        /// Grava o WM/MCDI como o WMP grava: bytes (tipo binário) com o texto
        /// UTF-16 dentro, terminado em zero.
        /// </summary>
        public bool DefinirMcdi(string strMcdi)
        {
            if (_cabecalho == null || string.IsNullOrEmpty(strMcdi)) return false;
            byte[] arr = Encoding.Unicode.GetBytes(strMcdi + "\0");
            IntPtr p = Marshal.AllocHGlobal(arr.Length);
            try
            {
                Marshal.Copy(arr, 0, p, arr.Length);
                _cabecalho.SetAttribute(NivelArquivo, "WM/MCDI", TipoAtributoAsf.Binario, p, (ushort)arr.Length);
                return true;
            }
            catch (Exception ex)
            {
                Falha = "Ao gravar 'WM/MCDI': " + ex.Message;
                return false;
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        public bool DefinirTexto(string nome, string valor)
        {
            if (_cabecalho == null) return false;
            IntPtr p = IntPtr.Zero;
            try
            {
                string v = valor ?? "";
                p = Marshal.StringToHGlobalUni(v);
                ushort bytes = (ushort)((v.Length + 1) * 2);
                _cabecalho.SetAttribute(NivelArquivo, nome, TipoAtributoAsf.Texto, p, bytes);
                return true;
            }
            catch (Exception ex)
            {
                Falha = "Ao gravar '" + nome + "': " + ex.Message;
                return false;
            }
            finally
            {
                if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            }
        }

        /// <summary>
        /// Grava um atributo GUID (WMT_TYPE_GUID, 16 bytes) no nível do
        /// arquivo. É o tipo de WM/MediaClassPrimaryID e SecondaryID.
        ///
        /// Por AddAttribute, não SetAttribute: o SetAttribute grava no Extended
        /// Content Description, que não aceita GUID — a sonda de 04/10/2026
        /// recebeu "O valor não recai no intervalo esperado". O AddAttribute
        /// põe no objeto que aceita (Metadata Library). Remove antes, para não
        /// ficar com dois valores.
        /// </summary>
        public bool DefinirGuid(string strNome, Guid guidValor)
        {
            if (_cabecalho == null) return false;
            byte[] arrBytes = guidValor.ToByteArray();
            IntPtr p = Marshal.AllocHGlobal(arrBytes.Length);
            try
            {
                Marshal.Copy(arrBytes, 0, p, arrBytes.Length);
                RemoverAtributo(strNome);
                ushort intIndice;
                _cabecalho.AddAttribute(NivelArquivo, strNome, out intIndice, TipoAtributoAsf.Guid, 0, p, (uint)arrBytes.Length);
                return true;
            }
            catch (Exception ex)
            {
                Falha = "Ao gravar '" + strNome + "': " + ex.Message;
                return false;
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WM_PICTURE_NATIVE
        {
            public IntPtr pwszMIMEType;
            public byte bPictureType;
            public IntPtr pwszDescription;
            public uint dwDataLen;
            public IntPtr pbData;
        }

        public void RemoverAtributo(string nome)
        {
            if (_cabecalho == null) return;
            try
            {
                ushort qtd = 0;
                _cabecalho.GetAttributeIndices(NivelArquivo, nome, IntPtr.Zero, IntPtr.Zero, ref qtd);
                if (qtd > 0)
                {
                    IntPtr pIndices = Marshal.AllocHGlobal(qtd * sizeof(ushort));
                    try
                    {
                        _cabecalho.GetAttributeIndices(NivelArquivo, nome, IntPtr.Zero, pIndices, ref qtd);
                        ushort[] arrIndices = new ushort[qtd];
                        for (int i = 0; i < qtd; i++)
                        {
                            arrIndices[i] = (ushort)Marshal.ReadInt16(pIndices, i * sizeof(ushort));
                        }
                        Array.Sort(arrIndices);
                        for (int i = arrIndices.Length - 1; i >= 0; i--)
                        {
                            _cabecalho.DeleteAttribute(NivelArquivo, arrIndices[i]);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pIndices);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// O tipo da imagem pelos primeiros bytes (a "assinatura" do formato),
        /// e não pelo que quem chama supõe: capa PNG declarada como JPEG não
        /// abre em alguns tocadores.
        /// </summary>
        public static string MimeDaImagem(byte[] arr)
        {
            if (arr == null || arr.Length < 8) return "image/jpeg";
            if (arr[0] == 0x89 && arr[1] == 0x50 && arr[2] == 0x4E && arr[3] == 0x47) return "image/png";
            if (arr[0] == 0x47 && arr[1] == 0x49 && arr[2] == 0x46) return "image/gif";
            if (arr[0] == 0x42 && arr[1] == 0x4D) return "image/bmp";
            return "image/jpeg";
        }

        /// <summary>Grava a capa frontal oficial em WM/Picture no formato binário WM_PICTURE_NATIVE. mime null: descobre pelos bytes.</summary>
        public bool DefinirImagem(byte[] dadosImagem, string mime = null, string descricao = "Cover")
        {
            if (_cabecalho == null || dadosImagem == null || dadosImagem.Length == 0) return false;
            if (mime == null) mime = MimeDaImagem(dadosImagem);

            RemoverAtributo("WM/Picture");

            IntPtr mimePtr = IntPtr.Zero;
            IntPtr descPtr = IntPtr.Zero;
            IntPtr imgPtr = IntPtr.Zero;
            IntPtr structPtr = IntPtr.Zero;

            try
            {
                mimePtr = Marshal.StringToCoTaskMemUni(mime ?? "image/jpeg");
                descPtr = Marshal.StringToCoTaskMemUni(descricao ?? "Cover");

                imgPtr = Marshal.AllocCoTaskMem(dadosImagem.Length);
                Marshal.Copy(dadosImagem, 0, imgPtr, dadosImagem.Length);

                WM_PICTURE_NATIVE pic = new WM_PICTURE_NATIVE
                {
                    pwszMIMEType = mimePtr,
                    bPictureType = 3, // Front Cover
                    pwszDescription = descPtr,
                    dwDataLen = (uint)dadosImagem.Length,
                    pbData = imgPtr
                };

                int structSize = Marshal.SizeOf(typeof(WM_PICTURE_NATIVE));
                structPtr = Marshal.AllocCoTaskMem(structSize);
                Marshal.StructureToPtr(pic, structPtr, false);

                ushort newIndex;
                _cabecalho.AddAttribute(NivelArquivo, "WM/Picture", out newIndex, TipoAtributoAsf.Binario, 0, structPtr, (uint)structSize);
                return true;
            }
            catch (Exception ex)
            {
                Falha = "Ao gravar WM/Picture: " + ex.Message;
                return false;
            }
            finally
            {
                if (structPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(structPtr);
                if (imgPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(imgPtr);
                if (descPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(descPtr);
                if (mimePtr != IntPtr.Zero) Marshal.FreeCoTaskMem(mimePtr);
            }
        }

        /// <summary>Confirma as alterações no arquivo. Sem isto, nada é gravado.</summary>
        public bool Salvar()
        {
            if (_editor == null) return false;
            try { _editor.Flush(); return true; }
            catch (Exception ex) { Falha = "Ao salvar: " + ex.Message; return false; }
        }

        public void Fechar()
        {
            if (_editor != null)
            {
                try { _editor.Close(); } catch { }
                try { Marshal.ReleaseComObject(_editor); } catch { }
            }
            _editor = null;
            _cabecalho = null;
        }

        public void Dispose() { Fechar(); }
    }
}
