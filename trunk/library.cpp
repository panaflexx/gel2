#include <limits.h>
#include <math.h>

#if _WINDOWS
const wchar_t kSeparator = L'\\';
#elif _UNIX
const wchar_t kSeparator = L'/';
#endif

// math

class Math {
 public:
   static double Sqrt(double d) {
     return sqrt(d);
   }
};

// I/O

class File {
public:
  static void Delete(String *path) {
    remove(MultiByteString(path).Get());
  }

  static bool Exists(String *path) {
    FILE *f = fopen(MultiByteString(path).Get(), "r");
    if (f != NULL) {
      fclose(f);
      return true;
    }
    return false;
  }

  static StringPtr ReadAllText(String *path) {
    const int bufsize = 128;
    char buf[bufsize];

    StringBuilder sb;
    FILE *f = fopen(MultiByteString(path).Get(), "r");
    _assert(f != NULL, L"file not found");
    while (fgets(buf, bufsize, f)) {
      DynamicString s(buf);   // convert to wide
      sb.Append(&s);
    }
    _assert(feof(f) != 0, L"expected end of file");
    return sb.ToString();
  }
};

class Path {
public:
  static StringPtr Combine(String *path1, String *path2) {
    StringBuilder sb;
    sb.Append(path1);
    if (!path1->EndsWithChar(kSeparator))
      sb.Append(kSeparator);
    sb.Append(path2);
    return sb.ToString();
  }

  static StringPtr GetDirectoryName(String *path) {
    int i = path->LastIndexOf(kSeparator);
    if (i == -1)
      return path;

    if (i == 0 ||   // "/"
        i == 2 && path->get_Item(1) == ':')
      return path->Substring(0, i + 1);  // include separator in directory name

    return path->Substring(0, i);   // exclude separator
  }

  static StringPtr GetExtension(String *path) {
    const wchar_t *p = path->Get();
    const wchar_t *e = wcsrchr(p, '.');
    if (!e)
      e = L"";
    return new DynamicString(e, true);
  }

  static StringPtr GetFileNameWithoutExtension(String *path) {
  int dot_pos = path->LastIndexOf(L'.');
    if (dot_pos >= 0)
      return path->Substring(0, dot_pos);
    else return path;
  }

  static StringPtr GetTempFileName() {
    char *f = tempnam(NULL, "_g_");
    _assert(f != NULL, L"can't get temporary file name");
    StringPtr s = new DynamicString(f);
    free(f);
    return s;
  }
};

class StreamReader : public Object {
  FILE *file_;

public:
  StreamReader(String *filename) {
    file_ = fopen(MultiByteString(filename).Get(), "r");
    _assert(file_ != NULL, L"file not found");
  }

  void Close() { fclose(file_); }

  int Read() {
    int c = getc(file_);
    return c == EOF ? -1 : c;
  }

  int Peek() {
    int c = getc(file_);
    ungetc(c, file_);
    return c == EOF ? -1 : c;
  }

  StringPtr ReadToEnd() {
    const int bufsize = 128;
    char buf[bufsize];

    StringBuilder sb;
    while (fgets(buf, bufsize, file_)) {
      DynamicString s(buf);   // convert to wide
      sb.Append(&s);
    }
    _assert(feof(file_) != 0, L"expected end of file");
    return sb.ToString();
  }
};

class StreamWriter : public Object {
  FILE *file_;

public:
  StreamWriter(String *filename) {
    file_ = fopen(MultiByteString(filename).Get(), "w");
    _assert(file_ != NULL, L"file not found");
  }

  StreamWriter(FILE *file) { file_ = file; }

  void Close() { fclose(file_); }

  void Write(const wchar_t *s) {
    fputs(MultiByteString(s).Get(), file_);
  }

public:
  void Write(Object *o) {
    if (o != NULL)
      Write(o->ToString()->Get());
  }

  void Write(String *s, Object *o) {
    Write(String::Format(s, o));
  }

  void Write(String *s, Object *o1, Object *o2) {
    Write(String::Format(s, o1, o2));
  }

  void Write(String *s, Object *o1, Object *o2, Object *o3) {
    Write(String::Format(s, o1, o2, o3));
  }

  void NewLine() { Write(L"\n"); }

  void WriteLine(Object *o) { Write(o); NewLine(); }
  void WriteLine(String *s, Object *o) { Write(s, o); NewLine(); }
  void WriteLine(String *s, Object *o1, Object *o2) { Write(s, o1, o2); NewLine(); }
  void WriteLine(String *s, Object *o1, Object *o2, Object *o3) { Write(s, o1, o2, o3); NewLine(); }
};

class Console {
  static StreamWriter w_;

public:
  static void Write(Object *o) { w_.Write(o); }
  static void Write(String *s, Object *o) { w_.Write(s, o); }
  static void Write(String *s, Object *o1, Object *o2) { w_.Write(s, o1, o2); }
  static void Write(String *s, Object *o1, Object *o2, Object *o3) { w_.Write(s, o1, o2, o3); }

  static void WriteLine(Object *o) { w_.WriteLine(o); }
  static void WriteLine(String *s, Object *o) { w_.WriteLine(s, o); }
  static void WriteLine(String *s, Object *o1, Object *o2) { w_.WriteLine(s, o1, o2); }
  static void WriteLine(String *s, Object *o1, Object *o2, Object *o3) { w_.WriteLine(s, o1, o2, o3); }
};

StreamWriter Console::w_(stdout);

// system

class PlatformID {
public:
  static const int Unix = 0, Win32NT = 1;
};

class OperatingSystem : public _Object {
public:
  int get_Platform() {
#if _WINDOWS
    return PlatformID::Win32NT;
#elif _UNIX
    return PlatformID::Unix;
#else
#error "unsupported platform"
#endif
  }
};

class Environment {
  static OperatingSystem os_;

public:
  static void Exit(int code) {
#if MEMORY_SAFE
    _exiting = true;
#endif
    exit(code);
  }

  static OperatingSystem *get_OSVersion() { return &os_; }

  static _Array<StringPtr> *_ArgArray(int argc, wchar_t *argv[]) {
    _assert(argc >= 1, L"main() received no argument");
    _Array<StringPtr> *a = new _CopyableArray<StringPtr>(&typeid(String *), argc - 1);
    for (int i = 1 ; i < argc ; ++i)
      a->get_Item(i - 1) = new String(argv[i]);
    return a;
  }

  /* Get an environment variable. */
  static StringPtr GetEnvironmentVariable(String * name) {
    char *v = getenv(MultiByteString(name).Get());
    return v != NULL ? new DynamicString(v) : NULL;
  }
};

OperatingSystem Environment::os_;

// A module of a process.  For now, we can only represent the main module of the current process.
class ProcessModule : public Object {
public:
  StringPtr get_FileName() {
#if _WINDOWS
    wchar_t path[MAX_PATH];
    DWORD n = GetModuleFileName(NULL, path, MAX_PATH);
    _assert(n > 0 && n < MAX_PATH, L"can't retrieve module path");
    return new DynamicString(path, true);
#elif _UNIX
    // This will work on Linux, but possibly not on other Unix systems.
    char buf[PATH_MAX];
    int n = readlink("/proc/self/exe", buf, PATH_MAX);
    buf[n] = '\0';
    return new DynamicString(buf);
#endif
  }
};

class Process : public Object {
public:
  static Process *GetCurrentProcess();

  void Unimplemented() { _assert(false, L"bad process operation"); }

  virtual ProcessModule *get_MainModule() { Unimplemented(); return 0; }

  static int System(String *command) {
    return system(MultiByteString(command).Get());
  }
};

class CurrentProcess : public Process {
public:
  virtual ProcessModule *get_MainModule() { return new ProcessModule(); }
};

/* static */ Process *Process::GetCurrentProcess() {
  return new CurrentProcess();
}
