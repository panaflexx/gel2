/*
Copyright (c) 2006 Google Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

#define UNICODE

// Symbols which may be defined in a build:
//
// MEMORY_SAFE - safe ownership types: keep non-owning reference counts
// EXTRA_SAFE - enable ref count checks which can fail only if there's a bug in the GEL2 compiler itself
//
// MEMORY_CRT - use CRT malloc() for allocating memory
// MEMORY_LEA - use Lea memory allocator
//
// Either MEMORY_CRT or MEMORY_LEA must be defined.
//

#if defined(_WIN32)
#define _WINDOWS 1
#elif defined(unix) || defined(__unix) || defined(__unix__)
#define _UNIX 1
#else
#error "unsupported platform"
#endif

#ifdef _MSC_VER   // Microsoft C++ compiler
#define _CRT_SECURE_NO_DEPRECATE
#define _CRT_NONSTDC_NO_DEPRECATE
#endif

#if !MEMORY_CRT && !MEMORY_LEA
#if _WINDOWS
#define MEMORY_LEA 1  // use Lea allocator by default
#else
#define MEMORY_CRT 1  // use CRT by default
#endif
#endif

#if MEMORY_CRT && _DEBUG && _WINDOWS
// #include <atldbgmem.h>
#include <crtdbg.h>
#endif

#include <ctype.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <typeinfo>
#include <wchar.h>
#include <wctype.h>
#include <errno.h>

#if _WINDOWS
#include <windows.h>
#include <new.h>  // for placement new

#elif _UNIX
#include <unistd.h> // _exit is declared here
#include <sys/types.h>
#include <sys/wait.h>
#include <new>
#endif

using std::type_info;

template <class T> class _Array;
class GlobalString;
class String;
class StringBuilder;

#if MEMORY_LEA
#define USE_DL_PREFIX 1
#include "dlmalloc.h"

inline void * __cdecl operator new(size_t n) {
  return dlmalloc(n);
}

inline void * __cdecl operator new[](size_t n) {
  return dlmalloc(n);
}

inline void __cdecl operator delete(void *p) {
  dlfree(p);
}

inline void __cdecl operator delete[](void *p) {
  dlfree(p);
}
#endif

void *Realloc(void *p, size_t size) {
#if MEMORY_CRT
  return realloc(p, size);
#elif MEMORY_LEA
  return dlrealloc(p, size);
#else
#error no memory allocator defined
#endif
}

void _assert(bool b, const wchar_t* message) {
  if (!b) {
    wprintf(L"runtime error: %s\n", message);
    _exit(1);
  }
}

// We use 5 different classes for wrapping pointers in GEL2:
//
// _Own - an owning pointer
// _Ptr - a non-owning pointer
// _Ref - a fully ref-counted object; currently only strings are fully ref-counted
// _OwnRef - an owning pointer or ref-counted object; we use this for object ^
// _PtrRef - a non-owning pointer or ref-counted object; we use this for object
//
// The classes _Ptr, _Ref, _OwnRef and _PtrRef each call a different set of functions
// for incrementing and decrementing reference counts, as follows:
//
// _Ptr: _PtrInc, _PtrDec (non-virtual)
// _Ref: _RefInc, _RefDec (non-virtual)
// _OwnRef: _OwnRefInc, _OwnRefDec (virtual)
// _PtrRef: _PtrRefInc, _PtrRefDec (virtual)

// a pointer to an owned object; cannot point to a string
template <class T> class _Own {
  T *p_;

public:
  _Own() { p_ = 0; }
  _Own(T *p) { p_ = p; }
  ~_Own() { delete p_; }

  T *Get() const { return p_; }
  T * operator -> () { return p_; }

  T* Take() { T *p = p_; p_ = 0; return p; }

  T * operator = (T *p) {
    if (p != p_) {
      delete p_;
      p_ = p;
    }
    return p;
  }

private:
  // disallow copy constructor and copy assignment
  _Own(const _Own<T> &o) { }
  _Own<T> & operator = (const _Own<T> &o) { }
};

#if MEMORY_SAFE
bool _exiting = false;

// a non-owning pointer
template <class T> class _Ptr {
  T *p_;

  void Init(T *p) { p_ = p; if (p_) p_->_PtrInc(); }

public:
  _Ptr() { p_ = 0; }
  _Ptr(T *p) { Init(p); }
  ~_Ptr() { if (!_exiting && p_) p_->_PtrDec(); }

  T * operator = (T *p) {
    if (p != p_) {
      if (p_) p_->_PtrDec();
      p_ = p;
      if (p_) p_->_PtrInc();
    }
    return p;
  }

  T *Get () const { return p_; }
  T * operator -> () { return p_; }

  // allow copy constructor and copy assignment
  _Ptr(const _Ptr<T> &p) { Init(p.p_); }
  _Ptr<T> & operator = (const _Ptr<T> & p) { *this = p.p_; return *this; }
};
#endif  // MEMORY_SAFE

// a pointer to a reference-counted object (i.e. to a string)
template <class T> class _Ref {
protected:
  T *p_;

  void Init(T *p) { p_ = p; if (p_) p->_RefInc(); }

public:
  _Ref() { p_ = 0; }
  _Ref(T *p) { Init(p); }
  ~_Ref() { if (p_) p_->_RefDec(); }

  T * operator = (T *p) {
    if (p != p_) {
      if (p_) p_->_RefDec();
      p_ = p;
      if (p_) p_->_RefInc();
    }
    return p;
  }

  T *Get () const { return p_; }
  operator T * () const { return p_; }
  T * operator -> () { return p_; }

  // allow copy constructor and copy assignment
  _Ref(const _Ref<T> &p) { Init(p.p_); }
  _Ref<T> & operator = (const _Ref<T> & p) { *this = p.p_; return *this; }
};

// a pointer to either an owned or reference-counted (string) object; we use this for Object ^
template <class T> class _OwnRef {
protected:
  T *p_;

public:
  _OwnRef() { p_ = 0; }
  _OwnRef(T *p) { p_ = p; if (p_) p_->_OwnRefInc(); }
  ~_OwnRef() { if (p_) p_->_OwnRefDec(); }

  T * operator = (T *p) {
    if (p != p_) {
      if (p_) p_->_OwnRefDec();
      p_ = p;
      if (p_) p_->_OwnRefInc();
    }
    return p;
  }

  T *Get () const { return p_; }
  T * operator -> () { return p_; }

  T* Take() { T *p = p_; if (p) p->_OwnSub(); p_ = 0; return p; }

private:
  // disallow copy constructor and copy assignment, since object might be owned
  _OwnRef(const _OwnRef<T> &o) { }
  _OwnRef<T> & operator = (const _OwnRef<T> &o) { }
};

// a pointer to either an unowned or reference-counted (string) object; we use this for Object
template <class T> class _PtrRef {
protected:
  T *p_;

  void Init(T *p) { p_ = p; if (p_) p->_PtrRefInc(); }

public:
  _PtrRef() { p_ = 0; }
  _PtrRef(T *p) { Init(p); }
  ~_PtrRef() { if (!_exiting && p_) p_->_PtrRefDec(); }

  T * operator = (T *p) {
    if (p != p_) {
      if (p_) p_->_PtrRefDec();
      p_ = p;
      if (p_) p_->_PtrRefInc();
    }
    return p;
  }

  T *Get () const { return p_; }
  T * operator -> () { return p_; }

  // allow copy constructor and copy assignment
  _PtrRef(const _PtrRef<T> &p) { Init(p.p_); }
  _PtrRef<T> & operator = (const _PtrRef<T> &p) { *this = p.p_; return *this; }
};

typedef _Ref<String> StringPtr;

#ifdef _MSC_VER
#pragma warning(push)
#pragma warning(disable: 4311)  // pointer truncation from 'type' to 'type'
#endif

static int TruncatePointer(void *ptr) {
  return reinterpret_cast<int>(ptr);
}

#ifdef _MSC_VER
#pragma warning(pop)
#endif

class Dummy {
};

// We set a high bit of an object's reference count to indicate that it's being destroyed
// in two phases and so its reference count may not be zero when ~_Object() first runs.
const int PendingDestroy = 0x70000000;

// _Object contains only non-virtual methods.  If a GEL2 program never uses a class as
// an Object, we derive the class from _Object to save a vtable pointer.
class _Object {
#if MEMORY_SAFE
 protected:
  int count_;
 public:
  _Object() : count_(0) { }

  ~_Object() {
    _assert(_exiting || count_ == 0 || (count_ & PendingDestroy),
      L"outstanding reference to destroyed object");
  }
#endif

public:
  void _PtrInc() {
#if MEMORY_SAFE
    ++count_;
#endif
  }

  void _PtrDec() {
#if MEMORY_SAFE
#if EXTRA_SAFE
    _assert(count_ > 0, "internal ref count failure");
#endif
    --count_;
#endif
  }

  template <class T> T _Addr() { return static_cast<T>(this); }

#if MEMORY_SAFE
  void _DeferDestroy() {
    count_ |= PendingDestroy;
  }

  void _CheckDeferredDestroy() {
    _assert(_exiting || count_ == PendingDestroy,
      L"outstanding reference to destroyed pool-allocated object");
  }
#endif
};

// In _Destroy1(), note that the constructor call is non-virtual;
// without the _Class:: prefix it would be virtual.
#define DESTROY1(_Class)        \
  virtual size_t _Destroy1() {  \
    this->_Class::~_Class();    \
    return sizeof(_Class);      \
  }

#if MEMORY_SAFE
#define GEL2_OBJECT(_Class)        \
  DESTROY1(_Class)              \
  virtual size_t _Destroy2() {  \
    return sizeof(_Class);      \
  }
#else
#define GEL2_OBJECT(_Class) DESTROY1(_Class)
#endif

class Object : public _Object {
public:
  virtual void _OwnRefInc() { }
  virtual void _OwnRefDec() { delete this; }
  virtual void _PtrRefInc() { _PtrInc(); }
  virtual void _PtrRefDec() { _PtrDec(); }

  virtual void _OwnSub() { }

  virtual ~Object() { }

  virtual bool Equals(Object *o) { return this == o; }

  virtual int GetHashCode() {
    return TruncatePointer(this);
  }

  static GlobalString object_string_;
  virtual StringPtr ToString();

  virtual size_t _Destroy1() { _assert(false, L"bad pool object"); return 0; }
#if MEMORY_SAFE
  virtual size_t _Destroy2() { _assert(false, L"bad pool object"); return 0; }
#endif
};

// Cast an (Object *) to the pointer type T.
template <class T> T _Cast(Object *o) {
  if (!o)
    return 0;
  T b = dynamic_cast<T>(o);
  _assert(b != 0, L"type cast failed");
  return b;
}

template <class T> T _Unbox(Object *o) {
  _assert(o != 0, L"unboxing conversion failed: source is null");
  T b = dynamic_cast<T>(o);
  _assert(b != 0, L"unboxing conversion failed");
  return b;
}

class String : public Object {
 protected:
  const wchar_t *s_;
  int length_;     // the number of characters in the string
#if !MEMORY_SAFE
  int count_;     // reference count for this object
#endif

  void _Dec() {
#if EXTRA_SAFE
    _assert(count_ > 0, "internal string ref count failure");
#endif
    --count_;
  }

 public:
  // (Note that _RefInc and _RefDec can't call _PtrInc and _PtrDec since those functions
  // do nothing in an unsafe build.)
  void _RefInc() { ++count_; }
  void _RefDec() { _Dec(); if (!count_) delete this; }

  virtual void _OwnRefInc() { _RefInc(); }
  virtual void _OwnRefDec() { _RefDec(); }
  virtual void _PtrRefInc() { _RefInc(); }
  virtual void _PtrRefDec() { _RefDec(); }

  virtual void _OwnSub() { _Dec(); }

 public:
  String(const wchar_t *s) {
#if !MEMORY_SAFE
    count_ = 0;
#endif
    s_ = s;
    length_ = wcslen(s_);
  }

  bool _Equals(const wchar_t *s) {
    return !wcscmp(s_, s);
  }

  static StringPtr New(_Array<wchar_t> *a);

  const wchar_t * Get() { return s_; }

  static bool _Equals(String *s1, String *s2) {
    return !s1 && !s2 ||
           s1 && s2 && s1->_Equals(s2->s_);
  }

  virtual bool Equals(Object *o) {
    return _Equals(this, dynamic_cast<String *>(o));
  }

  virtual int GetHashCode() {
    int h = 0;
    for (const wchar_t *s = s_; *s; ++s)
      h = h * 17 + *s;
    return h;
  }

  virtual StringPtr ToString() { return this; }

  static GlobalString empty_string_;

  static StringPtr _Concat(Object *o1, Object *o2);

  wchar_t get_Item(int index) {
    _assert(index >= 0 && index < length_, L"string index out of bounds");
    return s_[index];
  }

  static int CompareOrdinal(String *s, String *t) {
    return wcscmp(s->Get(), t->Get());
  }

  bool EndsWith(String *s) {
    return s->length_ <= length_ && 
           !wcscmp(s_ + length_ - s->length_, s->s_);
  }

  bool EndsWithChar(wchar_t c) {
    return length_ > 0 && s_[length_ - 1] == c;
  }

  static StringPtr Format(String *str, Object *o);
  static StringPtr Format(String *str, Object *o1, Object *o2);
  static StringPtr Format(String *str, Object *o1, Object *o2, Object *o3);

  /* 
   * Return the first index of a wide character c. 
   */
  int IndexOf(wchar_t c) {
    const wchar_t *p = wcschr(s_, c);
    return p ? static_cast<int>(p - s_) : -1;
  }

  /* Find the last index of c in the string. Returns -1 if c is not in 
   * the string. 
   */
  int LastIndexOf(wchar_t c) {
    const wchar_t *p = wcsrchr(s_, c);
    return p ? static_cast<int>(p - s_) : -1;
  }

  int get_Length() {
    return length_;
  }

  bool StartsWith(String *s) {
    return s->length_ <= length_ && !wcsncmp(s_, s->s_, s->length_);
  }

  StringPtr Substring(int start_index, int length);
};

class GlobalString : public String {
public:
  GlobalString(const wchar_t * s) : String(s) {
    _RefInc();
  }
};

// Duplicate a string.  We can't call wcsdup since we might not be using the CRT allocator.
const wchar_t * Duplicate(const wchar_t * s) {
  wchar_t * p = new wchar_t[wcslen(s) + 1];
  wcscpy(p, s);
  return p;
}

// Convert a multibyte string to a wide string; the caller must free the result.
const wchar_t *MultiByteToWide(const char *s) {
  int n = mbstowcs(NULL, s, 0) + 1;
  wchar_t *w = new wchar_t[n];
  mbstowcs(w, s, n);
  return w;
}

// Convert a wide string to a multibyte string; the caller must free the result.
const char *WideToMultiByte(const wchar_t *s) {
  int n = wcstombs(NULL, s, 0) + 1;
  char *t = new char[n];
  wcstombs(t, s, n);
  return t;
}

// a string whose characters are stored in heap-allocated memory
class DynamicString : public String {
public:
  DynamicString(const wchar_t * s) : String(s) { }
  DynamicString(const wchar_t * s, bool copy) : String(copy ? Duplicate(s) : s) { }

  DynamicString(const char *s) : String(MultiByteToWide(s)) { }

  ~DynamicString() { delete [] s_; }
};

/* static */ StringPtr String::_Concat(Object *o1, Object *o2) {
  StringPtr s1 = o1 == NULL ? NULL : o1->ToString();
  StringPtr s2 = o2 == NULL ? NULL : o2->ToString();
  if (s1 == NULL && s2 == NULL)
    return &empty_string_;
  if (s1 == NULL)
    return s2;
  if (s2 == NULL)
    return s1;

  int len1 = wcslen(s1->s_);
  int len2 = wcslen(s2->s_);
  wchar_t * s = new wchar_t[len1 + len2 + 1];
  memcpy(s, s1->s_, len1 * sizeof(wchar_t));
  memcpy(s + len1, s2->s_, len2 * sizeof(wchar_t));
  s[len1 + len2] = L'\0';
  return new DynamicString(s);
}

/* Returns a substring starting at start_index, with [length] characters. */
StringPtr String::Substring(int start_index, int length) {
  static const wchar_t out_of_bounds[] = L"substring index out of bounds";
  _assert(start_index >= 0, out_of_bounds);
  _assert(start_index + length <= length_, out_of_bounds);  
  wchar_t * s = new wchar_t[length + 1];
  memcpy(s, s_ + start_index, length * sizeof(wchar_t));
  s[length] = L'\0';
  return new DynamicString(s);
}

GlobalString Object::object_string_ = L"<object>";
GlobalString String::empty_string_ = L"";

/* virtual */ StringPtr Object::ToString() { return &object_string_; }

class MultiByteString {
  const char *m_;

public:
  MultiByteString(const wchar_t *s) {
    m_ = WideToMultiByte(s);
  }

  MultiByteString(String *s) {
    m_ = WideToMultiByte(s->Get());
  }

  ~MultiByteString() { delete [] m_; }

  const char *Get() { return m_; }
};

class Bool : public Object {
  bool b_;

public:
  Bool(bool b) { b_ = b; }
  bool Value() { return b_; }

  virtual bool Equals(Object *o) {
    Bool *b = dynamic_cast<Bool *>(o);
    return b != NULL && b_ == b->b_;
  }

  virtual int GetHashCode() { return b_ ? 1 : 0; }

  static GlobalString true_string_, false_string_;

  virtual StringPtr ToString() { return b_ ? &true_string_ : &false_string_; }
};

GlobalString Bool::true_string_ = L"True";
GlobalString Bool::false_string_ = L"False";

class Char : public Object {
  wchar_t c_;

public:
  Char(wchar_t c) { c_ = c; }
  wchar_t Value() { return c_; }

  virtual bool Equals(Object *o) {
    Char *c = dynamic_cast<Char *>(o);
    return c != NULL && c_ == c->c_;
  }

  virtual int GetHashCode() { return c_; }

  virtual StringPtr ToString() {
    wchar_t s[2];
    s[0] = c_;
    s[1] = L'\0';
    return new DynamicString(s, true);
  }

  static bool IsDigit(wchar_t c) {
    return iswdigit(c) != 0;
  }

  static bool IsLetter(wchar_t c) {
    return iswalpha(c) != 0;
  }

  static bool IsWhiteSpace(wchar_t c) {
    return iswspace(c) != 0;
  }
};

class NumberStyles {
public:
  static int const Integer, HexNumber;
};
int const NumberStyles::Integer = 0;
int const NumberStyles::HexNumber = 1;

class Int : public Object {
  int i_;

public:
  Int(int i) { i_ = i; }
  int Value() { return i_; }

  static int Max(int i, int j) { return i > j ? i : j; }

  virtual bool Equals(Object *o) {
    Int *i = dynamic_cast<Int *>(o);
    return (i != NULL && i_ == i->i_);
  }

  virtual int GetHashCode() { return i_; }

  virtual StringPtr ToString() {
    const int kBufSize = 20;
    wchar_t buf[kBufSize];
    swprintf(buf, kBufSize, L"%d", i_);
    return new DynamicString(buf, true);
  }

  static int Parse(String *s) {
    int i = 0;
    swscanf(s->Get(), L"%d", &i);
    return i;
  }

  static int ParseHex(String *s) {
    int i = 0;
    for (const wchar_t *p = s->Get(); *p ; ++p) {
      int d;
      wchar_t c = *p;
      if (c >= '0' && c <= '9')
        d = c - '0';
      else if (c >= 'a' && c <= 'f')
        d = 10 + c - 'a';
      else if (c >= 'A' && c <= 'F')
        d = 10 + c - 'A';
      else {
        _assert(false, L"bad hex digit");
        return 0;
      }
      i = 16 * i + d;
    }
    return i;
  }

  static int Parse(String *s, int style) {
    switch (style) {
      case NumberStyles::Integer:
        return Parse(s);
      case NumberStyles::HexNumber:
        return ParseHex(s);
      default:
        _assert(false, L"bad number style");
        return 0;
    }
  }
};

class Double : public Object {
  double d_;

public:
  Double(double d) { d_ = d; }
  double Value() { return d_; }

  virtual bool Equals(Object *o) {
    Double *d = dynamic_cast<Double *>(o);
    return (d != NULL && d_ == d->d_);
  }

  virtual int GetHashCode() {
    int *p = reinterpret_cast<int *>(&d_);
    return p[0] + p[1];
  }

  static StringPtr ToString(double d) {
    const int kBufSize = 20;
    wchar_t buf[kBufSize];
    swprintf(buf, kBufSize, L"%.10gel", d);
    return new DynamicString(buf, true);
  }

  virtual StringPtr ToString() {
    return ToString(d_);
  }

  static double Parse(String *s) {
    return wcstod(s->Get(), 0);
  }
};

class Single : public Object {
  float f_;

public:
  Single(float f) { f_ = f; }
  float Value() { return f_; }

  virtual bool Equals(Object *o) {
    Single *s = dynamic_cast<Single *>(o);
    return (s != NULL && f_ == s->f_);
  }

  virtual int GetHashCode() {
    return *(reinterpret_cast<int *>(&f_));
  }

  virtual StringPtr ToString() {
    return Double::ToString(f_);
  }

  static float Parse(String *s) {
    return static_cast<float>(Double::Parse(s));
  }
};

class Array : public Object {
 protected:
  const type_info *element_type_;
  int length_;

  Array(const type_info *element_type, int length)
    : element_type_(element_type), length_(length) { }

  void Check(int index) {
    _assert(index >= 0 && index < length_, L"array index out of bounds");
  }

 public:
  int get_Length() {
    return length_;
  }

  virtual void _Copy(int source_index, Array *dest, int dest_index, int length) = 0;

  static void Copy(Array *source, int source_index, Array *dest, int dest_index, int length) {
    _assert(*(source->element_type_) == *(dest->element_type_), L"can't copy between arrays of different types");
    static const wchar_t out_of_bounds[] = L"array copy index out of bounds";
    _assert(source_index >= 0, out_of_bounds);
    _assert(source_index + length <= source->length_, out_of_bounds);
    _assert(dest_index >= 0, out_of_bounds);
    _assert(dest_index + length <= dest->length_, out_of_bounds);
    source->_Copy(source_index, dest, dest_index, length);
  }

  void CopyTo(Array *arr, int index) {
    return Copy(this, 0, arr, index, length_);
  }
};

template <class T> class _Array : public Array {
protected:
  T *a_;

  _Array(const type_info *element_type, int length, T *init) : Array(element_type, length) {
    a_ = init;
  }

public:
  _Array(const type_info *element_type, int length) : Array(element_type, length) {
    a_ = new T[length];
    memset(a_, 0, length * sizeof(T));
  }

  ~_Array() {
    delete [] a_;
  }

  T &get_Item(int index) {
    Check(index);
    return a_[index];
  }

  T *get_location(int index) {
    Check(index);
    return &a_[index];
  }

  _Array<T> *CheckType(const type_info *type) {
    _assert(*element_type_ == *type, L"type cast failed: array has wrong type");
    return this;
  }

  virtual void _Copy(int source_index, Array * dest, int dest_index, int n) {
    _assert(false, L"can't copy elements between owning arrays");
  }
};

template <class T> class _CopyableArray : public _Array<T> {
protected:
  _CopyableArray(const type_info *element_type, int length, T *init)
    : _Array<T>(element_type, length, init) { }

public:
  _CopyableArray(const type_info *element_type, int length)
    : _Array<T>(element_type, length) { }

  virtual void _Copy(int source_index, Array *dest, int dest_index, int n) {
    _CopyableArray<T> *d = static_cast<_CopyableArray<T> *>(dest);
    // We can't use memmove since array elements may be reference-counted pointers.
    T *p = this->a_ + source_index;   // gcc 3.4.4 needs "this->" here
    T *end = p + n;
    T *q = d->a_ + dest_index;
    while (p < end)
      *q++ = *p++;
  }
};

// an array whose elements are statically allocated
template <class T> class _StaticArray : public _CopyableArray<T> {
public:
  _StaticArray(const type_info *element_type, int length, T *init)
  : _CopyableArray<T>(element_type, length, init) { }

  ~_StaticArray() {
    this->a_ = 0;  // gcc 3.4.4 needs "this->" here
  }

};

/* static */ StringPtr String::New(_Array<wchar_t> *a) {
  wchar_t *from = a->get_location(0);
  int len = a->get_Length();
  wchar_t *s = new wchar_t[len + 1];
  wcsncpy(s, from, len);
  s[len] = L'\0';
  return new DynamicString(s);
}

class StringBuilder : public Object {
  wchar_t * s_;
  int len_;
  int alloc_len_;

public:
  StringBuilder() {
    Init();
  }

  void Init() {
    len_ = alloc_len_ = 0;
    s_ = NULL;
  }

  /* Extend string builder by extra system characters. */
  void Extend(int extra) {
    if (len_ + extra > alloc_len_) {
      alloc_len_ = Int::Max(2 * (len_ + extra), 64);
      s_ = (wchar_t *) Realloc(s_, alloc_len_ * sizeof(wchar_t));
    }
  }

  void Append(wchar_t c) {
    Extend(1);
    s_[len_++] = c;
  }

  void Append(const wchar_t *start, int count) {
    Extend(count);
    memcpy(s_ + len_, start, count * sizeof(wchar_t));
    len_ += count;
  }

  void Append(const wchar_t *s) {
    Append(s, static_cast<int>(wcslen(s)));
  }

  void Append(String *s) {
    Append(s->Get());
  }

  void AppendFormat(String *str, Object *o1) {
    AppendFormat(str, o1, NULL, NULL);
  }

  void AppendFormat(String *str, Object *o1, Object *o2) {
    AppendFormat(str, o1, o2, NULL);
  }

  void AppendFormat(String *str, Object *o1, Object *o2, Object *o3) {
    const wchar_t *s = str->Get();
    const wchar_t *t;
    for (t = s; *t; ++t) {
      if (*t == L'{') {
        Append(s, static_cast<int>(t - s));
        Object *o;
        switch (*++t) {
          case '0': o = o1; break;
          case '1': o = o2; break;
          case '2': o = o3; break;
          default: o = NULL; _assert(false, L"bad format specifier");
        }
        Append(o->ToString());
        _assert(*++t == L'}', L"bad format specifier");
        s = t + 1;
      }
    }
    Append(s, static_cast<int>(t - s));
  }

  StringPtr ToString() {
    Append(L'\0');
    StringPtr s = new DynamicString(s_);
    Init();
    return s;
  }
};

StringPtr String::Format(String *str, Object *o) {
  StringBuilder sb;
  sb.AppendFormat(str, o);
  return sb.ToString();
}

StringPtr String::Format(String *str, Object *o1, Object *o2) {
  StringBuilder sb;
  sb.AppendFormat(str, o1, o2);
  return sb.ToString();
}

StringPtr String::Format(String *str, Object *o1, Object *o2, Object *o3) {
  StringBuilder sb;
  sb.AppendFormat(str, o1, o2, o3);
  return sb.ToString();
}

class PoolObject : public Object {
  Object *p_;

public:
  PoolObject(Object *p) { p_ = p; }

  virtual size_t _Destroy1() {
#if MEMORY_SAFE
    if (_exiting)   // single-pass destruction
      delete p_;
    else {
      p_->_DeferDestroy();

      // Invoke p_'s virtual destructor.  We don't bother to restore p_'s vtable since we
      // don't need to make any more virtual calls to it.
      p_->~Object();
    }
#else
    delete p_;
#endif

    return sizeof(PoolObject);
  }

#if MEMORY_SAFE
  virtual size_t _Destroy2() {
    p_->_CheckDeferredDestroy();   // non-virtual call: check ref count
    delete [] (char *) p_;
    return sizeof(PoolObject);
  }
#endif
};

class PoolBlock {
public:
  PoolBlock *prev_;
  char *first_;   // the first object allocated in this block
  char data_[1];
};

class Pool {
  PoolBlock *top_;  // linked list of fixed-size blocks

  static const unsigned int BlockSize = 8192;
  static const unsigned int Threshold = 256;

public:
  Pool() {
    top_ = 0;
    NewBlock();
  }

  void Destroy(PoolBlock *top, bool pass1) {
    PoolBlock *prev;
    for (PoolBlock *b = top ; b != 0 ; b = prev) {
      char *block_end = ((char *) b) + BlockSize;
      char *p = b->first_;
      while (p < block_end) {
        Object *o = (Object *) p;
#if MEMORY_SAFE
        if (_exiting) {
          p += o->_Destroy1();
          continue;
        }
        if (pass1) {
          o->_DeferDestroy();   // a non-virtual call

          // Running o's destructor will modify its vtable pointer; C++ effectively
          // modifies o's class during destruction to support its semantics for virtual
          // calls during object destruction (see e.g. C++ Primer, 15.4.5 "Virtuals in
          // Constructors and Destructors").  We want to be able to make a virtual call
          // to o during the second destruction pass, so we grab the vtable pointer here
          // and restore it after the call.
          // This might not work with some C++ compilers.
          void **v = (void **) p;
          void *vtable = *v;
          p += o->_Destroy1();
          *v = vtable;
        } else {  // second pass
          o->_CheckDeferredDestroy();   // a non-virtual call: check ref count
          p += o->_Destroy2();
        }
#else
          p += o->_Destroy1();
#endif
      }
      prev = b->prev_;
#if MEMORY_SAFE
      if (_exiting || !pass1)
        delete b;
#else
      delete b;
#endif
    }
  }

  ~Pool() {
    // Destroy a pool.  We make two passes since we want group destruction for pools:
    // strict destruction would be too limiting for users since objects in pools
    // appear in creation order, which may be unrelated to link structure.

    PoolBlock *top = top_;
    top_ = 0;

    // Make a first pass and run destructors.
    Destroy(top, true);

#if MEMORY_SAFE
    // Make a second pass and check reference counts.
    if (!_exiting)
      Destroy(top, false);
#endif
  }

  void NewBlock() {
    char *d = new char[BlockSize];
    PoolBlock *b = (PoolBlock *) d;
    b->first_ = d + BlockSize;
    b->prev_ = top_;
    top_ = b;
  }

  char *Alloc(size_t n) {
    _assert(top_ != 0, L"can't allocate from pool which is being destroyed");

    size_t free = top_->first_ - top_->data_;
    if (n <= free)
      return top_->first_ -= n;

    if (free < Threshold) {
      // We don't have much free space; allocate a new block.
      NewBlock();
      return Alloc(n);
    }

    // We don't have room; perform a non-pool allocation for this object.
    char *d = new char[n];
    new (Alloc(sizeof(_Own<Object>))) _Own<Object>((Object *) d);
    return d;
  }
};

class Debug {
public:
  static void Assert(bool b) {
    _assert(b, L"assertion failed");
  }
};

#if _MSC_VER
DWORD _exception_filter(EXCEPTION_POINTERS *p) {
  EXCEPTION_RECORD *r = p->ExceptionRecord;
  const char *error = 0;
  switch (r->ExceptionCode) {
    case EXCEPTION_INT_DIVIDE_BY_ZERO: error = "divide by zero"; break;
    case EXCEPTION_STACK_OVERFLOW: error = "stack overflow"; break;
    case EXCEPTION_ACCESS_VIOLATION:
      ULONG_PTR address = r->ExceptionInformation[1];
      if (address < 0x1000)
        error = "null pointer reference";
      break;
  }
  if (error) {
    printf("runtime error: %s\n", error);
    return EXCEPTION_EXECUTE_HANDLER;
  }
  return EXCEPTION_CONTINUE_SEARCH;
}
#endif

void _Initialize() {
#if MEMORY_CRT && _WINDOWS
#if _DEBUG 
  _CrtSetDbgFlag(_CRTDBG_ALLOC_MEM_DF | _CRTDBG_LEAK_CHECK_DF);
#endif

  intptr_t crt_heap = _get_heap_handle();
  ULONG enable = 2;
  if (!HeapSetInformation((HANDLE) crt_heap, HeapCompatibilityInformation, &enable, sizeof(enable)))
    puts("warning: could not enable low fragmentation heap");
#endif
}

void gel2_runmain_args(void (*gmain)(_Array<StringPtr> *), int argc, char *argv[]) {
  _assert(argc >= 1, L"main() received no argument");
  _Own<_CopyableArray<StringPtr> > a(new _CopyableArray<StringPtr>(&typeid(String *), argc - 1));
#if _WINDOWS
  // On Windows, we ignore argv[] and instead call GetCommandLine(), which gives us
  // arguments in Unicode.
  int wargc;
  wchar_t **wargv = CommandLineToArgvW(GetCommandLine(), &wargc);
  _assert(argc == wargc, L"argument count mismatch");
  for (int i = 1 ; i < argc ; ++i)
    a->get_Item(i - 1) = new String(wargv[i]);
  LocalFree(wargv);
#elif _UNIX
  for (int i = 1; i < argc; i++) {
    a->get_Item(i - 1) = new DynamicString(argv[i]);
  }
#endif
  gmain(a.Get());
}

int gel2_runmain2(void (*gmain)(), void (*gmain_a)(_Array<StringPtr > *), int argc, char *argv[]) {
#if _MSC_VER
  __try {
#endif
    _Initialize();
    if (gmain)
      gmain();
    else gel2_runmain_args(gmain_a, argc, argv);
    _exiting = true;
#if _MSC_VER
  } __except(_exception_filter(GetExceptionInformation())) {
    _exit(0);
  }
#endif
  return 0;
}

int gel2_runmain(void (*gmain)()) {
  return gel2_runmain2(gmain, 0, 0, 0);
}

int gel2_runmain(void (*gmain)(_Array<StringPtr > *), int argc, char *argv[]) {
  return gel2_runmain2(0, gmain, argc, argv);
}
