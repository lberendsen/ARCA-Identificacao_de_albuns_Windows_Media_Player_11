// ARCA Central — roda no notebook (Windows 11), do outro lado do cabo.
//
// O Vista não sai para a internet. Quem consulta Discogs e Apple Music é este
// serviço, com os MESMOS arquivos do Identificar (Comum\: DiscogsClient, ItunesResolver,
// CandidatoIdentificacao...), compilados aqui no .NET Framework 4.8, que tem
// TLS moderno. O Vista só pede, e só pode pedir o que está abaixo:
//
//   GET  /saude                         serviço vivo?
//   POST /buscar  {strArtista, strAlbum, intFaixas}
//                 -> Apple Music e Discogs em paralelo, um candidato de cada
//   POST /link    {strUrl, intFaixas}
//                 -> o lançamento exato de um link do Discogs ou do Apple Music
//   POST /discografia {strArtista}
//                 -> os discos do artista no Discogs (masters), com ano e faixas;
//                    na primeira vez só o andamento (blnPronta=false): pergunte de novo
//   POST /toc     {strMcdi}
//                 -> o disco pelo índice do CD que o WMP grava nas faixas (WM/MCDI):
//                    MusicBrainz (disc ID, capa do Cover Art Archive) e GnuDB
//   POST /accuraterip {strMcdi, intFaixasAudio}
//                 -> o arquivo da base do AccurateRip para o disco (base64); as
//                    assinaturas das faixas são calculadas no Vista
//   POST /offsets {}
//                 -> a tabela de offset dos leitores do AccurateRip (DriveOffsets.bin, base64),
//                    para o ARCA Ripar corrigir o deslocamento do leitor do Vista
//   POST /som     {lstCaminhos}
//                 -> o disco pelo SOM das faixas (fpcalc + AcoustID + MusicBrainz),
//                    lidas do Vista pelo compartilhamento; só a impressão digital sai
//   POST /faixa   {strArtista, strTitulo}
//                 -> as músicas do Apple Music para esse título (separação do
//                    Álbum Desconhecido); com cache e 3,2 s entre pedidos à rede
//   POST /digitalizar {dblLarguraCm, dblAlturaCm, intDpi}
//                 -> a G7000 (WIA/eSCL pela rede, sem diálogo); a resposta é o
//                    PNG em si (image/png), com a resolução em X-Dpi
//
// Escuta só num endereço (a ponta do cabo) e só atende a máquina do WMP ou a própria.
// Não é proxy: não há como pedir outro site. Os endereços vêm do central.ini ao lado do
// executável (veja Rede, abaixo); sem ele, os do cabo direto da instalação original:
//   escuta=192.168.50.1   cliente=192.168.50.2   porta=8130

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using ARCA.Modelo;

namespace ARCA.Central
{
    // TPedido, TCandidatoDto e TResposta estão em Protocolo.cs (compilado também no Vista).

    internal static class Programa
    {
        private static readonly int PORTA = Rede.Porta;
        private static readonly IPAddress IP_CABO = Rede.Escuta;
        private static readonly IPAddress IP_VISTA = Rede.Cliente;
        private const int MAX_CABECALHO = 16 * 1024;
        private const int MAX_CORPO = 64 * 1024;

        private static readonly object s_objTravaLog = new object();
        private static string s_strArquivoLog;

        private static int Main(string[] args)
        {
            Console.Title = "ARCA Central";
            Console.OutputEncoding = Encoding.UTF8;
            s_strArquivoLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "central.log");

            NetworkConfiguration.Initialize();   // TLS 1.2 e validação normal de certificados

            TcpListener objEscuta = new TcpListener(IP_CABO, PORTA);
            try { objEscuta.Start(); }
            catch (SocketException ex)
            {
                Registrar("Não consegui escutar em " + IP_CABO + ":" + PORTA + ": " + ex.Message +
                          ". A rede até a máquina do WMP está ligada e este computador tem o IP " + IP_CABO + "? (central.ini)");
                Console.ReadLine();
                return 1;
            }
            Registrar("ARCA Central pronto em http://" + IP_CABO + ":" + PORTA + "/  (atende só o Vista, 192.168.50.2)");

            while (true)
            {
                TcpClient objCliente = objEscuta.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(Atender, objCliente);
            }
        }

        /// <summary>
        /// Os endereços: central.ini ao lado do executável, linhas chave=valor (escuta, cliente, porta).
        /// </summary>
        internal static class Rede
        {
            public static readonly IPAddress Escuta = Ler("escuta", "192.168.50.1");
            public static readonly IPAddress Cliente = Ler("cliente", "192.168.50.2");
            public static readonly int Porta = int.Parse(Valor("porta", "8130"));

            private static IPAddress Ler(string strChave, string strPadrao) { return IPAddress.Parse(Valor(strChave, strPadrao)); }
            private static string Valor(string strChave, string strPadrao)
            {
                try
                {
                    string strArq = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "central.ini");
                    if (File.Exists(strArq))
                        foreach (string l in File.ReadAllLines(strArq))
                        {
                            int i = l.IndexOf('=');
                            if (i > 0 && l.Substring(0, i).Trim().Equals(strChave, StringComparison.OrdinalIgnoreCase)) return l.Substring(i + 1).Trim();
                        }
                }
                catch { }
                return strPadrao;
            }
        }

        private static void Registrar(string strTexto)
        {
            string strLinha = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + strTexto;
            lock (s_objTravaLog)
            {
                Console.WriteLine(strLinha);
                try { File.AppendAllText(s_strArquivoLog, strLinha + Environment.NewLine, Encoding.UTF8); } catch { }
            }
        }

        // ------------------------------------------------------------ HTTP mínimo

        private static void Atender(object objEstado)
        {
            TcpClient objCliente = (TcpClient)objEstado;
            IPAddress ipRemoto = ((IPEndPoint)objCliente.Client.RemoteEndPoint).Address;
            try
            {
                using (objCliente)
                using (NetworkStream ns = objCliente.GetStream())
                {
                    if (!ipRemoto.Equals(IP_VISTA) && !ipRemoto.Equals(IP_CABO))
                    {
                        Registrar("RECUSADO: conexão de " + ipRemoto);
                        return;
                    }
                    ns.ReadTimeout = 30000;
                    ns.WriteTimeout = 120000;

                    string strMetodo, strCaminho; int intTamanho;
                    if (!LerCabecalho(ns, out strMetodo, out strCaminho, out intTamanho))
                    {
                        Responder(ns, 400, "{\"strErro\":\"pedido malformado\"}");
                        return;
                    }
                    if (intTamanho > MAX_CORPO) { Responder(ns, 413, "{\"strErro\":\"pedido grande demais\"}"); return; }
                    byte[] arrCorpo = LerExato(ns, intTamanho);
                    string strCorpo = Encoding.UTF8.GetString(arrCorpo);

                    Registrar(ipRemoto + "  " + strMetodo + " " + strCaminho + (strCorpo.Length > 0 ? "  " + strCorpo : ""));
                    DateTime dtInicio = DateTime.Now;

                    string strResposta; int intStatus = 200;
                    JavaScriptSerializer jss = NovoSerializador();
                    if (strMetodo == "POST" && strCaminho == "/digitalizar")
                    {
                        TPedido objPed = jss.Deserialize<TPedido>(strCorpo) ?? new TPedido();
                        int intDpi = objPed.intDpi > 0 ? objPed.intDpi : 300;
                        byte[] arrPng;
                        try
                        {
                            arrPng = ScannerRede.Digitalizar(objPed.dblLarguraCm > 0 ? objPed.dblLarguraCm : 13,
                                                             objPed.dblAlturaCm > 0 ? objPed.dblAlturaCm : 13, intDpi,
                                                             delegate(string s) { Registrar("   " + s); });
                        }
                        catch (Exception ex)
                        {
                            Registrar("   -> scanner: " + ex.Message);
                            Responder(ns, 500, jss.Serialize(new TResposta { strErro = ex.Message }));
                            return;
                        }
                        ResponderBinario(ns, arrPng, "image/png", "X-Dpi: " + intDpi);
                        Registrar("   -> 200, PNG de " + arrPng.Length.ToString("N0") + " bytes, " +
                                  (int)(DateTime.Now - dtInicio).TotalMilliseconds + " ms");
                        return;
                    }
                    if (strMetodo == "GET" && strCaminho == "/saude")
                        strResposta = "{\"strServico\":\"ARCA Central\",\"strVersao\":\"3\",\"strHora\":\"" + DateTime.Now.ToString("s") + "\"}";
                    else if (strMetodo == "POST" && strCaminho == "/buscar")
                        strResposta = jss.Serialize(Buscar(jss.Deserialize<TPedido>(strCorpo)));
                    else if (strMetodo == "POST" && strCaminho == "/discografia")
                    {
                        TPedido objPed = jss.Deserialize<TPedido>(strCorpo) ?? new TPedido();
                        strResposta = jss.Serialize(Discografia.Pedir(objPed.strArtista, delegate(string s) { Registrar("   " + s); }));
                    }
                    else if (strMetodo == "POST" && strCaminho == "/faixa")
                    {
                        TPedido objPed = jss.Deserialize<TPedido>(strCorpo) ?? new TPedido();
                        strResposta = jss.Serialize(BuscaFaixa.Buscar(objPed.strArtista, objPed.strTitulo, delegate(string s) { Registrar("   " + s); }));
                    }
                    else if (strMetodo == "POST" && strCaminho == "/toc")
                    {
                        TPedido objPed = jss.Deserialize<TPedido>(strCorpo) ?? new TPedido();
                        TResposta objLog = new TResposta();
                        TResposta objR = ConsultaToc.Consultar(objPed.strMcdi, CriarLog(objLog));
                        objR.lstLog = objLog.lstLog;
                        strResposta = jss.Serialize(objR);
                    }
                    else if (strMetodo == "POST" && strCaminho == "/accuraterip")
                    {
                        // Só busca o arquivo da base: as assinaturas são calculadas no Vista, que tem o áudio.
                        TPedido objPed = jss.Deserialize<TPedido>(strCorpo) ?? new TPedido();
                        TResposta objR = new TResposta();
                        TTocAudio objToc = TTocAudio.DeMcdi(objPed.strMcdi, objPed.intFaixasAudio);
                        if (objToc == null) objR.strErro = "o índice do CD (WM/MCDI) não se lê";
                        else
                        {
                            objR.strUrl = AccurateRip.Url(objToc);
                            try
                            {
                                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(objR.strUrl);
                                req.Proxy = NetworkConfiguration.CreateProxy();
                                req.UserAgent = "ARCA";
                                req.Timeout = 20000;
                                using (HttpWebResponse resp = NetworkConfiguration.Responder(req))
                                using (Stream s = resp.GetResponseStream())
                                using (MemoryStream ms = new MemoryStream())
                                {
                                    s.CopyTo(ms);
                                    objR.strDadosBase64 = Convert.ToBase64String(ms.ToArray());
                                }
                                Registrar("   AccurateRip: " + objR.strUrl + " (" + objR.strDadosBase64.Length * 3 / 4 + " bytes)");
                            }
                            catch (WebException ex)
                            {
                                if (NetworkConfiguration.StatusHttp(ex) == 404) { objR.blnForaDaBase = true; Registrar("   AccurateRip: o disco não está na base"); }
                                else objR.strErro = "não consegui consultar o AccurateRip: " + ex.Message;
                            }
                        }
                        strResposta = jss.Serialize(objR);
                    }
                    else if (strMetodo == "POST" && strCaminho == "/offsets")
                    {
                        TResposta objR = new TResposta();
                        objR.strUrl = "http://www.accuraterip.com/accuraterip/DriveOffsets.bin";
                        try
                        {
                            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(objR.strUrl);
                            req.Proxy = NetworkConfiguration.CreateProxy();
                            req.UserAgent = "ARCA";
                            req.Timeout = 30000;
                            using (HttpWebResponse resp = NetworkConfiguration.Responder(req))
                            using (Stream s = resp.GetResponseStream())
                            using (MemoryStream ms = new MemoryStream())
                            {
                                s.CopyTo(ms);
                                objR.strDadosBase64 = Convert.ToBase64String(ms.ToArray());
                            }
                            Registrar("   offsets dos leitores: " + objR.strDadosBase64.Length * 3 / 4 + " bytes");
                        }
                        catch (WebException ex) { objR.strErro = "não consegui baixar a tabela de offsets: " + ex.Message; }
                        strResposta = jss.Serialize(objR);
                    }
                    else if (strMetodo == "POST" && strCaminho == "/som")
                    {
                        TPedido objPed = jss.Deserialize<TPedido>(strCorpo) ?? new TPedido();
                        TResposta objLog = new TResposta();
                        TResposta objR = BuscaSom.Consultar(objPed.lstCaminhos, CriarLog(objLog));
                        objR.lstLog = objLog.lstLog;
                        strResposta = jss.Serialize(objR);
                    }
                    else if (strMetodo == "POST" && strCaminho == "/link")
                        strResposta = jss.Serialize(PorLink(jss.Deserialize<TPedido>(strCorpo)));
                    else { intStatus = 404; strResposta = "{\"strErro\":\"não existe: " + strMetodo + " " + strCaminho + "\"}"; }

                    Responder(ns, intStatus, strResposta);
                    Registrar("   -> " + intStatus + ", " + strResposta.Length.ToString("N0") + " caracteres, " +
                              (int)(DateTime.Now - dtInicio).TotalMilliseconds + " ms");
                }
            }
            catch (Exception ex)
            {
                Registrar("ERRO atendendo " + ipRemoto + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static JavaScriptSerializer NovoSerializador()
        {
            JavaScriptSerializer jss = new JavaScriptSerializer();
            jss.MaxJsonLength = int.MaxValue;   // as capas vêm em base64
            return jss;
        }

        private static bool LerCabecalho(NetworkStream ns, out string strMetodo, out string strCaminho, out int intTamanho)
        {
            strMetodo = strCaminho = null; intTamanho = 0;
            MemoryStream ms = new MemoryStream();
            int b, intFim = 0;
            while (intFim < 4)
            {
                b = ns.ReadByte();
                if (b < 0 || ms.Length > MAX_CABECALHO) return false;
                ms.WriteByte((byte)b);
                intFim = (b == '\r' || b == '\n') ? intFim + 1 : 0;
                if (intFim == 2 && b == '\n' && ms.Length >= 2 && ms.GetBuffer()[ms.Length - 2] == '\n') break; // tolera \n\n
            }
            string[] vetLinhas = Encoding.ASCII.GetString(ms.ToArray()).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            string[] vetPrimeira = vetLinhas[0].Split(' ');
            if (vetPrimeira.Length < 2) return false;
            strMetodo = vetPrimeira[0].ToUpperInvariant();
            strCaminho = vetPrimeira[1];
            foreach (string strLinha in vetLinhas)
            {
                int i = strLinha.IndexOf(':');
                if (i > 0 && strLinha.Substring(0, i).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(strLinha.Substring(i + 1).Trim(), out intTamanho);
            }
            return true;
        }

        private static byte[] LerExato(NetworkStream ns, int intTamanho)
        {
            byte[] arr = new byte[intTamanho];
            int intLido = 0;
            while (intLido < intTamanho)
            {
                int r = ns.Read(arr, intLido, intTamanho - intLido);
                if (r <= 0) throw new IOException("conexão fechada no meio do pedido");
                intLido += r;
            }
            return arr;
        }

        private static void Responder(NetworkStream ns, int intStatus, string strJson)
        {
            byte[] arrCorpo = Encoding.UTF8.GetBytes(strJson);
            string strCab = "HTTP/1.1 " + intStatus + (intStatus == 200 ? " OK" : " Erro") + "\r\n" +
                            "Content-Type: application/json; charset=utf-8\r\n" +
                            "Content-Length: " + arrCorpo.Length + "\r\n" +
                            "Connection: close\r\n\r\n";
            byte[] arrCab = Encoding.ASCII.GetBytes(strCab);
            ns.Write(arrCab, 0, arrCab.Length);
            ns.Write(arrCorpo, 0, arrCorpo.Length);
            ns.Flush();
        }

        private static void ResponderBinario(NetworkStream ns, byte[] arrCorpo, string strTipo, string strCabecalhoExtra)
        {
            string strCab = "HTTP/1.1 200 OK\r\n" +
                            "Content-Type: " + strTipo + "\r\n" +
                            "Content-Length: " + arrCorpo.Length + "\r\n" +
                            strCabecalhoExtra + "\r\n" +
                            "Connection: close\r\n\r\n";
            byte[] arrCab = Encoding.ASCII.GetBytes(strCab);
            ns.Write(arrCab, 0, arrCab.Length);
            ns.Write(arrCorpo, 0, arrCorpo.Length);
            ns.Flush();
        }

        // ------------------------------------------------------------ busca

        /// <summary>
        /// Apple Music e Discogs ao mesmo tempo — o mesmo desenho do botão
        /// "Identificar álbum" da AQUISIÇÃO (AquisicaoView.xaml.cs).
        /// </summary>
        private static TResposta Buscar(TPedido objPedido)
        {
            TResposta objR = new TResposta();
            if (objPedido == null) { objR.strErro = "pedido vazio"; return objR; }
            string strArtista = (objPedido.strArtista ?? "").Trim();
            string strAlbum = (objPedido.strAlbum ?? "").Trim();
            int intFaixas = objPedido.intFaixas;
            if (strArtista.Length == 0 && strAlbum.Length == 0) { objR.strErro = "informe artista ou álbum"; return objR; }

            Action<string> actLog = CriarLog(objR);

            Task<TCandidatoIdentificacao> objTarefaApple = Task.Factory.StartNew(delegate
            {
                try
                {
                    TItunesAlbumInfo objAlbum = ItunesResolver.ConsultarAlbum(strArtista, strAlbum, intFaixas, false, actLog);
                    if (objAlbum == null) return null;
                    return TCandidatoIdentificacao.DeItunes(objAlbum, BaixarCapaApple(objAlbum, actLog));
                }
                catch (Exception ex) { actLog("Apple Music: falha na consulta: " + ex.Message); return null; }
            });

            Task<TCandidatoIdentificacao> objTarefaDiscogs = Task.Factory.StartNew(delegate
            {
                try
                {
                    return TCandidatoIdentificacao.DeDiscogs(DiscogsClient.ConsultarLancamento(strArtista, strAlbum, intFaixas, actLog));
                }
                catch (Exception ex) { actLog("Discogs: falha na consulta: " + ex.Message); return null; }
            });

            objR.objApple = TCandidatoDto.De(objTarefaApple.Result);
            objR.objDiscogs = TCandidatoDto.De(objTarefaDiscogs.Result);
            if (objR.objApple == null && objR.objDiscogs == null)
                objR.strErro = "nem o Apple Music nem o Discogs acharam '" + (strArtista + " — " + strAlbum).Trim(' ', '—') + "'";
            return objR;
        }

        /// <summary>Link exato: lê aquele lançamento e não cai numa busca por texto.</summary>
        private static TResposta PorLink(TPedido objPedido)
        {
            TResposta objR = new TResposta();
            string strUrl = objPedido == null ? "" : (objPedido.strUrl ?? "").Trim();
            Action<string> actLog = CriarLog(objR);
            try
            {
                if (DiscogsClient.EhLink(strUrl))
                    objR.objCandidato = TCandidatoDto.De(TCandidatoIdentificacao.DeDiscogs(DiscogsClient.ConsultarPorLink(strUrl, actLog)));
                else if (ItunesResolver.EhLinkOuId(strUrl))
                {
                    TItunesAlbumInfo objAlbum = ItunesResolver.ConsultarPorUrlOuId(strUrl, objPedido.intFaixas, actLog);
                    if (objAlbum != null)
                        objR.objCandidato = TCandidatoDto.De(TCandidatoIdentificacao.DeItunes(objAlbum, BaixarCapaApple(objAlbum, actLog)));
                }
                else
                {
                    objR.strErro = "não é um link do Discogs (/release/ ou /master/) nem do Apple Music";
                    return objR;
                }
                if (objR.objCandidato == null) objR.strErro = "o link não trouxe um lançamento";
            }
            catch (Exception ex) { objR.strErro = "falha ao ler o link: " + ex.Message; }
            return objR;
        }

        private static byte[] BaixarCapaApple(TItunesAlbumInfo objAlbum, Action<string> actLog)
        {
            string strUrlCapa = objAlbum.strArtworkUrl1400 ?? objAlbum.strArtworkUrl600 ?? objAlbum.strArtworkUrl100;
            if (string.IsNullOrEmpty(strUrlCapa)) return null;
            try { return ExternalMetadataClient.FetchImage(strUrlCapa); }
            catch (Exception ex) { actLog("Apple Music: falha ao baixar a capa: " + ex.Message); return null; }
        }

        private static Action<string> CriarLog(TResposta objR)
        {
            return delegate(string strMsg)
            {
                lock (objR.lstLog) objR.lstLog.Add(strMsg);
                Registrar("   " + strMsg);
            };
        }
    }
}
