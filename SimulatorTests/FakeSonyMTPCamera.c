// Fake SonyMTPCamera.dll: same C API as dougforpres/SonyCamera SonyMTPCamera.h, simulated camera
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

typedef uint32_t DWORD; typedef uint16_t WORD; typedef uint16_t WCHAR;
typedef struct { DWORD version, w, h, wc, hc, bx, by, crop; double emin, emax, estep, pw, ph; DWORD bpp;
                 WCHAR *manufacturer, *model, *serial, *deviceName, *sensorName, *deviceVersion; } DEVICEINFO;
typedef struct { DWORD flags, w, h, wc, hc, pw, ph, bx, by; double pxw, pxh; } CAMERAINFO;
typedef struct { DWORD size; uint8_t* data; DWORD status, imageMode, width, height, flags; double duration; } IMAGEINFO;
typedef struct { DWORD value; WCHAR* name; } PROPERTYVALUEOPTION;
typedef struct { DWORD id, value; WCHAR* text; } PROPERTYVALUE;
typedef struct { DWORD id; WORD type, flags; WCHAR* name; DWORD valueCount; } PROPERTYDESCRIPTOR;
typedef struct { WCHAR *id, *manufacturer, *model, *devicePath; } PORTABLEDEVICEINFO;

enum { F_FAIL_OPEN = 1, F_FAIL_START = 2, F_TRUNCATE = 4, F_FAIL_PREVIEW = 8, F_NO_ISO = 16, F_FAIL_INFO = 32, F_BAD_OPTION = 64 };
static int infoActiveCalls = 0, cancels = 0, cancelled = 0, startCalls = 0;
static int W = 6000, H = 4000, PW = 1024, PH = 680, FLAGS = 0, polls = 0, mode = 1;
static int closes = 0; static DWORD lastClosed = 0; static DWORD iso = 800;
static const DWORD isoOpts[] = { 100, 200, 400, 800, 0x00ffffff };

static WCHAR* ws(const char* s) { size_t n = strlen(s); WCHAR* r = malloc((n + 1) * 2); for (size_t i = 0; i <= n; i++) r[i] = (unsigned char)s[i]; return r; }
static uint8_t* image(int w, int h, int bpp, DWORD* size) {
    size_t n = (size_t)w * h * bpp; uint8_t* p = malloc(n); uint16_t* q = (uint16_t*)p;
    for (size_t i = 0; i < n / 2; i++) q[i] = (uint16_t)((i * 2654435761u) >> 13);
    *size = (DWORD)((FLAGS & F_TRUNCATE) ? n / 2 : n); return p;
}

void Fake_Configure(int w, int h, int flags) { W = w; H = h; FLAGS = flags; closes = 0; lastClosed = 0; iso = 800; infoActiveCalls = 0; cancels = 0; cancelled = 0; startCalls = 0; polls = 0; }
void Fake_Counters(int* info, int* c, int* starts) { *info = infoActiveCalls; *c = cancels; *starts = startCalls; }
void Fake_Stats(int* c, DWORD* last, DWORD* currentIso) { *c = closes; *last = lastClosed; *currentIso = iso; }

DWORD GetDeviceCount() { return 1; }
DWORD GetPortableDeviceCount() { return 1; }
DWORD GetPortableDeviceInfo(DWORD id, PORTABLEDEVICEINFO* p) { p->id = ws("cam1"); p->manufacturer = ws("Sony"); p->model = ws("ILCE-7M3"); p->devicePath = ws("usb#1"); return 0; }
DWORD OpenDevice(WCHAR* name) { return (FLAGS & F_FAIL_OPEN) ? 0xffffffffu : 5; }
void CloseDevice(DWORD h) { closes++; lastClosed = h; }
DWORD GetDeviceInfo(DWORD h, DEVICEINFO* d) {
    d->w = W; d->h = H; d->wc = W; d->hc = H; d->bx = 0; d->by = 0; d->crop = 0; d->emin = 1.0 / 8000; d->emax = 3600; d->estep = 0.001; d->pw = 5.9; d->ph = 5.9; d->bpp = 14;
    d->manufacturer = ws("Sony"); d->model = ws("ILCE-7M3"); d->serial = ws("00012345"); d->deviceName = ws("cam1"); d->sensorName = ws("IMX410"); d->deviceVersion = ws("1.0");
    return 0;
}
DWORD GetCameraInfo(DWORD h, CAMERAINFO* c, DWORD f) {
    if (FLAGS & F_FAIL_INFO) return 1168; // ERROR_NOT_FOUND
    if (f & 1) infoActiveCalls++;
    c->flags = 1; c->w = W; c->h = H; c->wc = W; c->hc = H; c->pw = PW; c->ph = PH; c->pxw = 5.9; c->pxh = 5.9; return 0;
}
DWORD GetPreviewImage(DWORD h, IMAGEINFO* i) {
    if (FLAGS & F_FAIL_PREVIEW) return 0; // real DLL swallows its exception and leaves status untouched
    i->data = image(PW, PH, 6, &i->size); i->width = PW; i->height = PH; i->duration = 0; i->status = 4; return 0;
}
DWORD StartCapture(DWORD h, IMAGEINFO* i) {
    i->status = 0x8001;
    if (FLAGS & F_FAIL_START) { i->status = 2; return 1223; } // ERROR_CANCELLED
    mode = i->imageMode; polls = 3; cancelled = 0; startCalls++; return 0;
}
DWORD GetCaptureStatus(DWORD h, IMAGEINFO* i) {
    if (cancelled) { i->status = 3; return 0; }
    if (polls > 0) { polls--; i->status = polls == 0 ? 0x8003 : 1; return 0; }
    int bpp = mode == 1 ? 2 : 6;
    i->data = image(W, H, bpp, &i->size); i->width = W; i->height = H; i->imageMode = mode; i->status = 4; return 0;
}
DWORD CancelCapture(DWORD h, IMAGEINFO* i) { cancels++; if (polls > 0) cancelled = 1; polls = 0; return 0; }
DWORD RefreshPropertyList(DWORD h) { return 0; }
DWORD GetPropertyList(DWORD h, DWORD* list, DWORD* size) {
    DWORD n = (FLAGS & F_NO_ISO) ? 1 : 2;
    if (!list) { *size = n; return 1237; } // ERROR_RETRY
    list[0] = 0xd21e; if (n > 1) list[1] = 0xfffe; return 0;
}
DWORD GetPropertyDescriptor(DWORD h, DWORD id, PROPERTYDESCRIPTOR* d) { d->id = id; d->type = 6; d->flags = 2; d->name = ws(id == 0xd21e ? "ISO" : "ISO Options"); d->valueCount = id == 0xfffe ? 5 : 0; return 0; }
DWORD GetPropertyValueOption(DWORD h, DWORD id, PROPERTYVALUEOPTION* o, DWORD idx) { if ((FLAGS & F_BAD_OPTION) && idx == 2) return 87; o->value = isoOpts[idx]; o->name = ws("iso"); return 0; }
DWORD GetSinglePropertyValue(DWORD h, DWORD id, PROPERTYVALUE* v) { v->id = id; v->value = iso; v->text = ws("800"); return 0; }
DWORD GetAllPropertyValues(DWORD h, void* v, DWORD* c) { return 0; }
DWORD SetPropertyValue(DWORD h, DWORD id, DWORD value) { if (id == 0xd21e) iso = value; return 0; }
DWORD SetExposureTime(DWORD h, float t, PROPERTYVALUE* v) { return 0; }
