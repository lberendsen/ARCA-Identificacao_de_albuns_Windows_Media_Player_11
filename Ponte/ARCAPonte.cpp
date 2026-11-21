// ARCAPonte.cpp - a peca do ARCA que mora dentro do WMP 11 (wmplayer.exe, 32 bits).
//
// Plug-in de IU em segundo plano, que aceita midia ("Enviar para").
// Faz so duas coisas, e nao toca na rede nem nos arquivos de musica:
//
//  1. Em "Enviar para > ARCA: identificar album", recebe do WMP as faixas
//     selecionadas (SAFEARRAY de IWMPMedia), grava a lista num arquivo
//     temporario e abre o ARCAIdentificar.exe, que fica na mesma pasta desta DLL.
//
//  2. Desvia "Localizar Informacoes sobre o Album" (WM_COMMAND 7312, postado na
//     lista da biblioteca) para o item do ARCA em "Enviar para". O id desse item
//     e dinamico: e lido do proprio submenu quando o menu de contexto abre.
//
// O desvio foi provado por uma sonda dentro do WMP antes de virar produto (19/11/2026).
// Log curto em %TEMP%\ARCA\ponte.log.

#define _WIN32_WINNT 0x0600
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <ole2.h>
#include <olectl.h>
#include <stdio.h>
#include <stdarg.h>
#include <wmp.h>
#include <wmpplug.h>

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "advapi32.lib")

// {EF03AEB5-0BA5-4ACE-9A5F-BDBF1FF99321}
static const CLSID CLSID_ArcaPonte = {0xef03aeb5,0x0ba5,0x4ace,{0x9a,0x5f,0xbd,0xbf,0x1f,0xf9,0x93,0x21}};
static const wchar_t* STR_CLSID = L"{EF03AEB5-0BA5-4ACE-9A5F-BDBF1FF99321}";
static const wchar_t* STR_NOME  = L"ARCA: identificar álbum";   // e o texto do item em "Enviar para"
static const UINT ID_LOCALIZAR_ALBUM = 7312;
static const UINT ID_ENVIAR_PARA     = 7726;

static HINSTANCE        g_hInst = NULL;
static LONG             g_lngObjetos = 0, g_lngTravas = 0, g_lngDonosGancho = 0;
static CRITICAL_SECTION g_cs;
static bool             g_blnCs = false;
static HHOOK            g_hkChamada = NULL, g_hkFila = NULL;
static UINT             g_uintIdArca = 0;

// ---------------------------------------------------------------- log

static void PastaTemporariaArca(wchar_t* buf, size_t n)
{
    wchar_t tmp[MAX_PATH]; GetTempPathW(MAX_PATH, tmp);
    swprintf_s(buf, n, L"%sARCA", tmp);
    CreateDirectoryW(buf, NULL);
}

static void Registrar(const wchar_t* strFormato, ...)
{
    if (!g_blnCs) return;
    wchar_t buf[1024];
    va_list a; va_start(a, strFormato);
    _vsnwprintf_s(buf, _countof(buf), _TRUNCATE, strFormato, a);
    va_end(a);
    wchar_t pasta[MAX_PATH], arq[MAX_PATH];
    PastaTemporariaArca(pasta, MAX_PATH);
    swprintf_s(arq, L"%s\\ponte.log", pasta);
    SYSTEMTIME st; GetLocalTime(&st);
    EnterCriticalSection(&g_cs);
    FILE* f = NULL;
    if (_wfopen_s(&f, arq, L"a, ccs=UTF-8") == 0 && f)
    {
        fwprintf(f, L"%04d-%02d-%02d %02d:%02d:%02d  %s\n", st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, buf);
        fclose(f);
    }
    LeaveCriticalSection(&g_cs);
}

// ---------------------------------------------------------------- abrir o ARCA Identificar

static void AbrirIdentificar(SAFEARRAY* psa, IWMPPlaylist* pl)
{
    // Junta os caminhos das faixas.
    const int MAX_ITENS = 2000;
    BSTR* vetUrls = new BSTR[MAX_ITENS];
    int intN = 0;
    if (psa)
    {
        VARTYPE vt = VT_EMPTY; SafeArrayGetVartype(psa, &vt);
        LONG lo = 0, hi = -1; SafeArrayGetLBound(psa, 1, &lo); SafeArrayGetUBound(psa, 1, &hi);
        for (LONG i = lo; i <= hi && intN < MAX_ITENS; i++)
        {
            IUnknown* pu = NULL;
            if (vt == VT_UNKNOWN || vt == VT_DISPATCH) { if (FAILED(SafeArrayGetElement(psa, &i, &pu))) pu = NULL; }
            else if (vt == VT_VARIANT)
            {
                VARIANT v; VariantInit(&v);
                if (SUCCEEDED(SafeArrayGetElement(psa, &i, &v)) && (v.vt == VT_UNKNOWN || v.vt == VT_DISPATCH) && v.punkVal) { pu = v.punkVal; pu->AddRef(); }
                VariantClear(&v);
            }
            if (!pu) continue;
            IWMPMedia* m = NULL;
            if (SUCCEEDED(pu->QueryInterface(__uuidof(IWMPMedia), (void**)&m)) && m)
            {
                BSTR url = NULL;
                if (SUCCEEDED(m->get_sourceURL(&url)) && url) vetUrls[intN++] = url;
                m->Release();
            }
            pu->Release();
        }
    }
    if (pl)
    {
        long c = 0; pl->get_count(&c);
        for (long i = 0; i < c && intN < MAX_ITENS; i++)
        {
            IWMPMedia* m = NULL;
            if (SUCCEEDED(pl->get_item(i, &m)) && m)
            {
                BSTR url = NULL;
                if (SUCCEEDED(m->get_sourceURL(&url)) && url) vetUrls[intN++] = url;
                m->Release();
            }
        }
    }
    if (intN == 0) { Registrar(L"Enviar para: nenhuma faixa com caminho"); delete[] vetUrls; return; }

    // Lista em UTF-8 com BOM, uma faixa por linha.
    wchar_t pasta[MAX_PATH], lista[MAX_PATH];
    PastaTemporariaArca(pasta, MAX_PATH);
    swprintf_s(lista, L"%s\\identificar-%lu.txt", pasta, GetTickCount());
    FILE* f = NULL;
    if (_wfopen_s(&f, lista, L"w, ccs=UTF-8") == 0 && f)
    {
        for (int i = 0; i < intN; i++) fwprintf(f, L"%s\n", vetUrls[i]);
        fclose(f);
    }
    for (int i = 0; i < intN; i++) SysFreeString(vetUrls[i]);
    delete[] vetUrls;

    // O programa fica ao lado desta DLL.
    wchar_t dll[MAX_PATH], exe[MAX_PATH];
    GetModuleFileNameW(g_hInst, dll, MAX_PATH);
    wcscpy_s(exe, dll);
    wchar_t* barra = wcsrchr(exe, L'\\');
    if (barra) *(barra + 1) = 0;
    wcscat_s(exe, L"ARCAIdentificar.exe");

    wchar_t cmd[2 * MAX_PATH + 8];
    swprintf_s(cmd, L"\"%s\" \"%s\"", exe, lista);
    STARTUPINFOW si; ZeroMemory(&si, sizeof(si)); si.cb = sizeof(si);
    PROCESS_INFORMATION pi; ZeroMemory(&pi, sizeof(pi));
    if (CreateProcessW(exe, cmd, NULL, NULL, FALSE, 0, NULL, NULL, &si, &pi))
    {
        Registrar(L"ARCA Identificar aberto com %d faixa(s), lista %s", intN, lista);
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
    }
    else
    {
        Registrar(L"FALHA ao abrir %s (erro %lu)", exe, GetLastError());
        MessageBoxW(NULL, L"Não consegui abrir o ARCAIdentificar.exe, que deveria estar ao lado de ARCAPonte.dll.",
                    STR_NOME, MB_OK | MB_ICONERROR);
    }
}

// ---------------------------------------------------------------- ganchos (o desvio do 7312)

static void ProcurarItemArca(HMENU hm, int intNivel)
{
    int c = GetMenuItemCount(hm);
    for (int i = 0; i < c && i < 100; i++)
    {
        MENUITEMINFOW mii; ZeroMemory(&mii, sizeof(mii)); mii.cbSize = sizeof(mii);
        wchar_t txt[256] = L"";
        mii.fMask = MIIM_ID | MIIM_STRING | MIIM_SUBMENU;
        mii.dwTypeData = txt; mii.cch = 255;
        if (!GetMenuItemInfoW(hm, i, TRUE, &mii)) continue;
        if (wcscmp(txt, STR_NOME) == 0) { g_uintIdArca = mii.wID; return; }
        if (mii.wID == ID_ENVIAR_PARA && mii.hSubMenu && intNivel < 2) ProcurarItemArca(mii.hSubMenu, intNivel + 1);
    }
}

static LRESULT CALLBACK GanchoChamada(int nCode, WPARAM wp, LPARAM lp)
{
    if (nCode == HC_ACTION && ((CWPSTRUCT*)lp)->message == WM_INITMENUPOPUP)
        ProcurarItemArca((HMENU)((CWPSTRUCT*)lp)->wParam, 0);
    return CallNextHookEx(g_hkChamada, nCode, wp, lp);
}

static LRESULT CALLBACK GanchoFila(int nCode, WPARAM wp, LPARAM lp)
{
    if (nCode == HC_ACTION && wp == PM_REMOVE)
    {
        MSG* m = (MSG*)lp;
        if (m->message == WM_COMMAND && m->lParam == 0 && LOWORD(m->wParam) == ID_LOCALIZAR_ALBUM)
        {
            if (g_uintIdArca)
            {
                m->wParam = MAKEWPARAM(g_uintIdArca, HIWORD(m->wParam));
                Registrar(L"Localizar Informações sobre o Álbum -> ARCA (item %u)", g_uintIdArca);
            }
            else Registrar(L"Localizar Informações sobre o Álbum: item do ARCA não encontrado no menu; segue o do WMP");
        }
    }
    return CallNextHookEx(g_hkFila, nCode, wp, lp);
}

static void InstalarGanchos()
{
    if (InterlockedIncrement(&g_lngDonosGancho) != 1) return;
    g_hkChamada = SetWindowsHookExW(WH_CALLWNDPROC, GanchoChamada, NULL, GetCurrentThreadId());
    g_hkFila    = SetWindowsHookExW(WH_GETMESSAGE, GanchoFila, NULL, GetCurrentThreadId());
    Registrar(L"ponte carregada no WMP; ganchos %s", (g_hkChamada && g_hkFila) ? L"ok" : L"FALHARAM");
}

static void RemoverGanchos()
{
    if (InterlockedDecrement(&g_lngDonosGancho) != 0) return;
    if (g_hkChamada) UnhookWindowsHookEx(g_hkChamada);
    if (g_hkFila) UnhookWindowsHookEx(g_hkFila);
    g_hkChamada = g_hkFila = NULL;
}

// ---------------------------------------------------------------- o plug-in

class CPonte : public IWMPPluginUI
{
    LONG m_lngRef;
    bool m_blnGancho;
public:
    CPonte() : m_lngRef(1), m_blnGancho(false) { InterlockedIncrement(&g_lngObjetos); }
    ~CPonte() { if (m_blnGancho) RemoverGanchos(); InterlockedDecrement(&g_lngObjetos); }

    STDMETHODIMP QueryInterface(REFIID riid, void** ppv)
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == __uuidof(IWMPPluginUI)) { *ppv = static_cast<IWMPPluginUI*>(this); AddRef(); return S_OK; }
        *ppv = NULL; return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() { return InterlockedIncrement(&m_lngRef); }
    STDMETHODIMP_(ULONG) Release() { LONG r = InterlockedDecrement(&m_lngRef); if (r == 0) delete this; return r; }

    STDMETHODIMP SetCore(IWMPCore* pCore)
    {
        if (pCore && !m_blnGancho) { InstalarGanchos(); m_blnGancho = true; }
        else if (!pCore && m_blnGancho) { RemoverGanchos(); m_blnGancho = false; }
        return S_OK;
    }
    STDMETHODIMP Create(HWND, HWND*) { return E_NOTIMPL; }
    STDMETHODIMP Destroy() { return S_OK; }
    STDMETHODIMP DisplayPropertyPage(HWND) { return E_NOTIMPL; }
    STDMETHODIMP GetProperty(const WCHAR*, VARIANT* pvar) { if (pvar) VariantInit(pvar); return E_NOTIMPL; }
    STDMETHODIMP SetProperty(const WCHAR* pwszName, const VARIANT* pv)
    {
        if (!pwszName || !pv) return S_OK;
        bool blnMidia = wcscmp(pwszName, PLUGIN_ALL_MEDIASENDTO) == 0;
        bool blnLista = wcscmp(pwszName, PLUGIN_ALL_PLAYLISTSENDTO) == 0;
        if (!blnMidia && !blnLista) return S_OK;
        Receber(pv);
        return S_OK;
    }
    STDMETHODIMP TranslateAccelerator(LPMSG) { return S_FALSE; }

private:
    // Nada aqui pode derrubar o WMP: qualquer falha vira linha no log.
    static void Receber(const VARIANT* pv)
    {
        try
        {
            if ((pv->vt & VT_ARRAY) && pv->parray) { AbrirIdentificar(pv->parray, NULL); return; }
            if ((pv->vt == VT_DISPATCH || pv->vt == VT_UNKNOWN) && pv->punkVal)
            {
                IWMPPlaylist* pl = NULL;
                if (SUCCEEDED(pv->punkVal->QueryInterface(__uuidof(IWMPPlaylist), (void**)&pl)) && pl)
                {
                    AbrirIdentificar(NULL, pl); pl->Release(); return;
                }
                SAFEARRAYBOUND b = { 1, 0 };
                SAFEARRAY* psa = SafeArrayCreate(VT_UNKNOWN, 1, &b);
                LONG i = 0; SafeArrayPutElement(psa, &i, pv->punkVal);
                AbrirIdentificar(psa, NULL);
                SafeArrayDestroy(psa);
                return;
            }
            Registrar(L"Enviar para: formato inesperado (vt=%u)", pv->vt);
        }
        catch (...) { Registrar(L"Enviar para: exceção tratada"); }
    }
};

class CFabrica : public IClassFactory
{
    LONG m_lngRef;
public:
    CFabrica() : m_lngRef(1) {}
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv)
    {
        if (riid == IID_IUnknown || riid == IID_IClassFactory) { *ppv = this; AddRef(); return S_OK; }
        *ppv = NULL; return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() { return InterlockedIncrement(&m_lngRef); }
    STDMETHODIMP_(ULONG) Release() { LONG r = InterlockedDecrement(&m_lngRef); if (r == 0) delete this; return r; }
    STDMETHODIMP CreateInstance(IUnknown* pOuter, REFIID riid, void** ppv)
    {
        if (pOuter) return CLASS_E_NOAGGREGATION;
        CPonte* p = new CPonte();
        HRESULT hr = p->QueryInterface(riid, ppv);
        p->Release();
        return hr;
    }
    STDMETHODIMP LockServer(BOOL f)
    {
        if (f) InterlockedIncrement(&g_lngTravas); else InterlockedDecrement(&g_lngTravas);
        return S_OK;
    }
};

// ---------------------------------------------------------------- exportacoes COM

BOOL WINAPI DllMain(HINSTANCE h, DWORD dwMotivo, LPVOID)
{
    if (dwMotivo == DLL_PROCESS_ATTACH)
    {
        g_hInst = h;
        DisableThreadLibraryCalls(h);
        InitializeCriticalSection(&g_cs); g_blnCs = true;
    }
    else if (dwMotivo == DLL_PROCESS_DETACH && g_blnCs)
    {
        g_blnCs = false; DeleteCriticalSection(&g_cs);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    if (rclsid != CLSID_ArcaPonte) return CLASS_E_CLASSNOTAVAILABLE;
    CFabrica* f = new CFabrica();
    HRESULT hr = f->QueryInterface(riid, ppv);
    f->Release();
    return hr;
}

STDAPI DllCanUnloadNow()
{
    return (g_lngObjetos == 0 && g_lngTravas == 0 && g_lngDonosGancho == 0) ? S_OK : S_FALSE;
}

static LONG GravarTexto(const wchar_t* chave, const wchar_t* nome, const wchar_t* valor)
{
    HKEY h; LONG r = RegCreateKeyExW(HKEY_LOCAL_MACHINE, chave, 0, NULL, 0, KEY_WRITE, NULL, &h, NULL);
    if (r != ERROR_SUCCESS) return r;
    r = RegSetValueExW(h, nome, 0, REG_SZ, (const BYTE*)valor, (DWORD)((wcslen(valor) + 1) * sizeof(wchar_t)));
    RegCloseKey(h); return r;
}

static LONG GravarDword(const wchar_t* chave, const wchar_t* nome, DWORD valor)
{
    HKEY h; LONG r = RegCreateKeyExW(HKEY_LOCAL_MACHINE, chave, 0, NULL, 0, KEY_WRITE, NULL, &h, NULL);
    if (r != ERROR_SUCCESS) return r;
    r = RegSetValueExW(h, nome, 0, REG_DWORD, (const BYTE*)&valor, sizeof(valor));
    RegCloseKey(h); return r;
}

STDAPI DllRegisterServer()
{
    wchar_t dll[MAX_PATH]; GetModuleFileNameW(g_hInst, dll, MAX_PATH);
    wchar_t chave[256];
    swprintf_s(chave, L"Software\\Classes\\CLSID\\%s", STR_CLSID);
    if (GravarTexto(chave, NULL, STR_NOME)) return SELFREG_E_CLASS;
    swprintf_s(chave, L"Software\\Classes\\CLSID\\%s\\InprocServer32", STR_CLSID);
    if (GravarTexto(chave, NULL, dll)) return SELFREG_E_CLASS;
    if (GravarTexto(chave, L"ThreadingModel", L"Apartment")) return SELFREG_E_CLASS;
    swprintf_s(chave, L"%s\\%s", PLUGIN_INSTALLREGKEY, STR_CLSID);
    if (GravarTexto(chave, PLUGIN_INSTALLREGKEY_FRIENDLYNAME, STR_NOME)) return SELFREG_E_CLASS;
    if (GravarTexto(chave, PLUGIN_INSTALLREGKEY_DESCRIPTION,
                    L"Localizar Informações sobre o Álbum pelo ARCA: Discogs e Apple Music lado a lado, capa e tags, áudio conferido."))
        return SELFREG_E_CLASS;
    if (GravarDword(chave, PLUGIN_INSTALLREGKEY_CAPABILITIES,
                    PLUGIN_TYPE_BACKGROUND | PLUGIN_FLAGS_ACCEPTSMEDIA | PLUGIN_FLAGS_ACCEPTSPLAYLISTS | PLUGIN_FLAGS_INSTALLAUTORUN))
        return SELFREG_E_CLASS;
    return S_OK;
}

STDAPI DllUnregisterServer()
{
    wchar_t chave[256];
    swprintf_s(chave, L"%s\\%s", PLUGIN_INSTALLREGKEY, STR_CLSID);
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, chave);
    swprintf_s(chave, L"Software\\Classes\\CLSID\\%s", STR_CLSID);
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, chave);
    return S_OK;
}
