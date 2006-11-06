/*
Copyright (c) 2006 Google Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy of 
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to use,
copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the
Software, and to permit persons to whom the Software is furnished to do so, subject
to the following conditions:

The above copyright notice and this permission notice shall be included in all copies
or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE
OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

class Syntax {
  public readonly string file_;
  public readonly int line_;

  public Syntax() { file_ = Gel.CurrentFile(); line_ = Gel.Line(); }

  static void Report(string file, int line, string type) {
    Console.Write("{0}({1}): ", file, line);
    Console.Write("{0}: ", type);
    if (Gel.error_test_)
      Gel.error_lines_.Add(line);
  }

  public void Error(string message) {
    Report(file_, line_, "error");
    Console.WriteLine(message);
  }

  public void Error(string format, object arg) {
    Report(file_, line_, "error");
    Console.WriteLine(format, arg);
  }

  public void Error(string format, object arg1, object arg2) {
    Report(file_, line_, "error");
    Console.WriteLine(format, arg1, arg2);
  }

  public void Error(string format, object arg1, object arg2, object arg3) {
    Report(file_, line_, "error");
    Console.WriteLine(format, arg1, arg2, arg3);
  }

  public void Warning(string message) {
    Report(file_, line_, "warning");
    Console.WriteLine(message);
  }
}

abstract class TypeExpr : Syntax {
  public abstract GType Resolve(Program program);
}

class ConversionContext {
  public const int Other = 0,
                   AssignVar = 1,
                   MethodArg = 2;
}

// GEL2 includes the following kinds of types:
// - simple types (int, bool, char, float, double): SimpleType
// - owned types (including array types): ArrayType or Class with Owned() => true
// - owning types: Owning
// - string: GString
// - null type: NullType
// - void: VoidType

abstract class GType {
  public virtual bool IsOwned() { return false; }

  // A reference type is an owned, owning, string or null type.
  public virtual bool IsReference() { return true; }

  // A value type is a simple or string type.
  public virtual bool IsValue() { return false; }

  public virtual Class Parent() { return GObject.type_; }
  
  // If this is an owning type T ^ then return T; otherwise return this.
  public virtual GType BaseType() { return this; }

  // Return the set of types which may be destroyed when a variable of this type
  // is destroyed.
  public virtual TypeSet VarDestroys() { return TypeSet.empty_; }

  // Return the set of types which may be destroyed when a value of this concrete type
  // is destroyed.
  public virtual TypeSet TypeDestroys() { return TypeSet.empty_; }

  public virtual void FindVarDestroys(int marker, TypeSet set) { }

  public virtual void FindTypeDestroys(int marker, TypeSet set) { }

  public virtual bool IsSubtype(GType type) {
    for (GType t = this; t != null ; t = t.Parent())
      if (type.Equals(t))
        return true;
    return false;
  }

  protected virtual bool CanConvertOwnership(GType t, int context) {
    bool from_owning = this is Owning;
    bool to_owning = t is Owning;
    if (IsValue() && t.BaseType() == GObject.type_) {
      Debug.Assert(!from_owning);
      // boxing conversion yields an owning pointer; string->object conversion may
      // yield an owning or non-owning pointer
      return to_owning || context == ConversionContext.MethodArg || this == GString.type_;
    }
    if (BaseType() == GObject.type_ && t.IsValue()) {
      Debug.Assert(!to_owning);
      return true;  // an unboxing or object->string conversion, OK in any context
    }
    return from_owning == to_owning ||
           from_owning && !to_owning &&
             (context == ConversionContext.AssignVar || context == ConversionContext.MethodArg);
  }

  // Ensure that this class will inherit from Object in generated C++ code.
  public virtual void SetObjectInherit() { }

  // Return true if there is a non-subtyping implicit conversion from this to t.
  public virtual bool CanConvert1(GType t) { return false; }

  // Return true if there is a non-subtyping explicit conversion from this to t.
  protected virtual bool CanConvertExplicit1(GType t) { return false; }

  // Return true if this type can be converted to type [to].
  public bool CanConvert(GType to, int context, bool is_explicit, bool subtype_only) {
    if (!CanConvertOwnership(to, context))
      return false;

    GType from_base = BaseType();
    GType to_base = to.BaseType();
    if (from_base.Equals(to_base))
      return true;   // identity conversion

    // If we ever perform a conversion to C ^, then C must have a vtable so that we can call
    // a virtual destructor when an owning pointer is destroyed.
    if (to is Owning && this != Null.type_)
      to_base.SetVirtual();

    // If we ever perform an explicit conversion from a class, that class must
    // have a vtable so that every instance of the class will have run-time type information.
    if (is_explicit)
      from_base.SetVirtual();

    // If we ever convert between C and Object, then C must derive from Object in generated code.
    if (from_base == GObject.type_)
      to_base.SetObjectInherit();
    else if (to_base == GObject.type_)
      from_base.SetObjectInherit();

    return from_base.IsSubtype(to_base) ||
           !subtype_only && from_base.CanConvert1(to_base) ||
           is_explicit && (to_base.IsSubtype(from_base) ||
                           !subtype_only && from_base.CanConvertExplicit1(to_base));
  }

  public bool CanConvert(GType t, int context) { return CanConvert(t, context, false, false); }
  public bool CanConvert(GType t) { return CanConvert(t, ConversionContext.Other); }

  public bool CanConvertExplicit(GType t, bool subtype_only) {
    return CanConvert(t, ConversionContext.Other, true, subtype_only);
  }

  public bool CheckConvert(Syntax caller, GType t, int context) {
    if (!CanConvert(t, context)) {
      caller.Error("can't convert {0} to {1}", this, t);
      return false;
    }
    return true;
  }

  public bool CheckConvert(Syntax caller, GType t) {
    return CheckConvert(caller, t, ConversionContext.Other);
  }

  // Return a type to which the types [this] and t can both be implicitly converted.
  public GType CommonType(Syntax caller, GType t) {
    if (CanConvert(t))
      return t;
    if (t.CanConvert(this))
      return this;
    caller.Error("incompatible types: {0} and {1}", this, t);
    return null;
  }

  public abstract SimpleValue DefaultValue();

  static ArrayList empty_array_ = new ArrayList();
  public virtual ArrayList /* of Member */ Members() { return empty_array_; }

  public Member GetMatchingMember(Member m1) {
    foreach (Member m in Members())
      if (m.MatchSignature(m1))
        return m;
    return null;
  }

  // Find a match for m in this type or its ancestors, ignoring private members and
  // optionally ignoring overrides.
  public Member FindMatchingMember(Member m, bool override_ok) {
    for (GType t = this ; t != null ; t = t.Parent()) {
      Member m1 = t.GetMatchingMember(m);
      if (m1 != null && !m1.IsPrivate() && (override_ok || !m1.IsOverride()))
        return m1;
    }
    return null;
  }

  int Score(bool accessible, int mismatches) {
    return (accessible ? 0 : 100) + mismatches;
  }

  public Member Lookup(Syntax caller, Class from_class, bool through_base,
                       int kind, string name, ArrayList /* of Argument */ arguments, bool report_error) {
    Member m1 = null;   // the best match we've found so far
    bool found_any = false;
    bool accessible = false;
    int score = -1;

    GType this_type = BaseType();
    GType t;
    for (t = this_type; t != null; t = (kind == MemberKind.Constructor ? null : t.Parent())) {
      foreach (Member m in t.Members())
        if (Member.MatchKind(m.Kind(), kind) && m.name_ == name && !m.IsOverride()) {
          found_any = true;
          int mismatches = 0;
          if (arguments != null && !m.IsApplicable(arguments, out mismatches))
            continue; // wrong argument count
          bool new_accessible = m.IsAccessible(from_class, this_type, through_base);
          int new_score = Score(new_accessible, mismatches);
          if (new_score == 0 && score == 0) {
            if (report_error)
              caller.Error("{0}: ambiguous {1} call", name, Member.ToString(kind));
            return null;
          }
          if (score == -1 || new_score < score) {
            m1 = m;
            score = new_score;
            accessible = new_accessible;
          }
        }
      if (score == 0)
        break;
    }

    if (score != 0) {
      if (report_error) {
        string k = Member.ToString(kind);
        if (!found_any)
          caller.Error("{0}: {1} not found", name, k);
        else if (score == -1) {
          string a = String.Format("{0} argument{1}", arguments.Count, arguments.Count == 1 ? "" : "s");
          caller.Error("{0}: no {1} overload takes {2}", name, k, a);
        } else if (!accessible)
          caller.Error("{0}: can't access {1} {2}",
                       name, m1.IsProtected() ? "protected" : "private", k);
        else {
          caller.Error("{0}: the best overloaded {1} match has some invalid arguments", name, k);
          m1.ReportMismatches(caller, arguments);
        }
      }
      return null;
    }

    // If we've found a member of the Object class, then we're using this class as an object,
    // so in generated code it must ultimately inherit from Object, not _Object.
    if (t == GObject.type_)
      this_type.SetObjectInherit();

    if (through_base) {
      // As a special case, whenever we're accessing through base we find the member which will
      // actually be invoked; this is not necessarily the same as the member we've just found
      // if the member is virtual.
      m1 = FindMatchingMember(m1, true);
      Debug.Assert(m1 != null);
    }

    return m1;
  }

  public virtual void SetVirtual() { }

  // Emit the name of the C++ class we use to hold instances of this type.
  public virtual string EmitTypeName() { Debug.Assert(false); return null; }

  // Return a C++ type of the form "T<U>".  If U ends with a '>', we emit an
  // extra space to avoid emitting the token >>, which will cause GCC to complain.
  public static string ConstructType(string generic, string type) {
    return String.Format("{0}<{1}{2}>", generic, type,
                         type.EndsWith(">") ? " " : "");
  }

  string EmitNonOwningPointer(string name) {
    if (Gel.program_.safe_)
      return ConstructType(this == GObject.type_ ? "_PtrRef" : "_Ptr", name);
    return String.Format("{0} *", name);
  }

  // Emit a C++ type used for variables holding instances of this type.
  public virtual string EmitType() {
    return EmitNonOwningPointer(EmitTypeName());
  }

  // Emit a C++ type used for expressions holding instances of this type.
  public virtual string EmitExprType() {
    return EmitTypeName() + " *";
  }

  public virtual string EmitReturnType() {
    return EmitExprType();
  }

  public virtual string EmitGenericType() {
    return IsReference() ? EmitNonOwningPointer("Object") : EmitType();
  }
}

class TypeSet {
  ArrayList /* of GType */ types_ = new ArrayList();

  public static readonly TypeSet empty_ = new TypeSet();

  public void Add(GType type) {
    for (int i = 0; i < types_.Count; ++i) {
      GType t = (GType) types_[i];
      if (type.IsSubtype(t))
        return;
      if (t.IsSubtype(type)) {
        types_[i++] = type;
        while (i < types_.Count) {
          t = (GType) types_[i];
          if (t.IsSubtype(type))
            types_.RemoveAt(i);
          else ++i;
        }
        return;
      }
    }
    types_.Add(type);
  }

  public void Add(TypeSet set) {
    foreach (GType t in set.types_)
      Add(t);
  }

  public bool Contains(GType type) {
    foreach (GType t in types_)
      if (type.IsSubtype(t))
        return true;
    return false;
  }

  public bool IsObject() {
    return types_.Count > 0 && types_[0] == GObject.type_;
  }

  public override string ToString() {
    StringBuilder sb = new StringBuilder();
    sb.Append("{");
    foreach (GType t in types_)
      sb.AppendFormat(" {0}", t.ToString());
    sb.Append(" }");
    return sb.ToString();
  }
}

class VoidType : GType {
  public override bool IsReference() { Debug.Assert(false); return false; }

  public override SimpleValue DefaultValue() { Debug.Assert(false); return null; }

  public override string ToString() { return "void"; }

  public override string EmitExprType() { return "void"; }
  public override string EmitType() { return "void"; }
}

class Void {
  public static readonly GType type_ = new VoidType();
}

// a GValue or a Location containing a GValue
abstract class ValueOrLocation { }

abstract class GValue : ValueOrLocation {
  public abstract GType Type();

  public virtual GValue Get(Field field) { Debug.Assert(false); return null; }

  public virtual GValue ConvertExplicit(GType t) {
    // try implicit conversion
    GValue v = Convert(t);
    if (v != this || Type().IsSubtype(t) )
      return v;

    Console.WriteLine("type cast failed: object of type {0} is not a {1}", Type(), t);
    Gel.Exit();
    return null;
  }

  // Implicitly convert this value to type t.
  public virtual GValue Convert(GType t) {
    return this;
  }

  public virtual bool DefaultEquals(object o) { return Equals(o); }
  public virtual int DefaultHashCode() { return GetHashCode(); }
  public virtual string DefaultToString() { return ToString(); }

  public virtual GValue Invoke(Method m, ValueList args) {
    switch (m.name_) {
      case "Equals": return new GBool(DefaultEquals(args.Object()));
      case "GetHashCode": return new GInt(DefaultHashCode());
      case "ToString": return new GString(DefaultToString());
      default: Debug.Assert(false); return null;
    }
  }
}

abstract class SimpleValue : GValue {
  public abstract string Emit();
}

class GBool : SimpleValue {
  public readonly bool b_;

  public GBool(bool b) { b_ = b; }

  public override bool Equals(object o) {
    GBool b = o as GBool;
    return b != null && b.b_ == b_;
  }

  public override int GetHashCode() { return b_.GetHashCode(); }

  public static readonly BoolClass type_ = new BoolClass();

  public override GType Type() { return type_; }

  public override string ToString() { return b_.ToString(); }

  public override string Emit() { return b_ ? "true" : "false"; }
}

class GInt : SimpleValue {
  public readonly int i_;

  public GInt(int i) { i_ = i; }

  public static readonly IntClass type_ = new IntClass();

  public override GType Type() { return type_; }

  public override bool Equals(object o) {
    GInt i = o as GInt;
    return i != null && i.i_ == i_;
  }

  public override int GetHashCode() { return i_.GetHashCode(); }

  public override GValue Convert(GType t) {
    if (t == GFloat.type_)
      return new GFloat(i_);
    if (t == GDouble.type_)
      return new GDouble(i_);
    return base.Convert(t);
  }

  public override GValue ConvertExplicit(GType t) {
    return t == GChar.type_ ? new GChar((char) i_) : base.ConvertExplicit(t);
  }

  public override string ToString() { return i_.ToString(); }

  public override string Emit() { return i_.ToString(); }
}

class GFloat : SimpleValue {
  public readonly float f_;

  public GFloat(float f) { f_ = f; }

  public static readonly FloatClass type_ = new FloatClass();

  public override GType Type() { return type_; }

  public override bool Equals(object o) {
    GFloat f = o as GFloat;
    return f != null && f.f_ == f_;
  }

  public override int GetHashCode() { return f_.GetHashCode(); }

  public override GValue Convert(GType t) {
    if (t == GDouble.type_)
      return new GDouble(f_);
    return base.Convert(t);
  }

  public override GValue ConvertExplicit(GType t) {
    return t == GInt.type_ ? new GInt((int) f_) : base.ConvertExplicit(t);
  }

  public override string ToString() { return f_.ToString(); }

  public override string Emit() {
    string s = f_.ToString();
    return s + (s.IndexOf('.') == -1 ? ".0f" : "f");
  }
}

class GDouble : SimpleValue {
  public readonly double d_;

  public GDouble(double d) { d_ = d; }

  public static readonly DoubleClass type_ = new DoubleClass();

  public override GType Type() { return type_; }

  public override bool Equals(object o) {
    GDouble d = o as GDouble;
    return d != null && d.d_ == d_;
  }

  public override int GetHashCode() { return d_.GetHashCode(); }

  public override GValue ConvertExplicit(GType t) {
    if (t == GInt.type_)
      return new GInt((int) d_);
    if (t == GFloat.type_)
      return new GFloat((float) d_);
    return base.ConvertExplicit(t);
  }

  public override string ToString() { return d_.ToString(); }

  public override string Emit() {
    string s = d_.ToString();
    if (s.IndexOf('.') == -1)
      s = s + ".0";
    return s;
  }
}

class GChar : SimpleValue {
  public readonly char c_;

  public GChar(char c) { c_ = c; }

  public static readonly CharClass type_ = new CharClass();

  public override GType Type() { return type_; }

  public override bool Equals(object o) {
    GChar c = o as GChar;
    return c != null && c.c_ == c_;
  }

  public override int GetHashCode() { return c_.GetHashCode(); }

  public override GValue Convert(GType t) {
    return t == GInt.type_ ? new GInt(c_) : base.Convert(t);
  }

  public override string ToString() { return c_.ToString(); }

  public static string Emit(char c) {
    switch (c) {
      case '\0': return "\\0";
      case '\n': return "\\n";
      case '\r': return "\\r";
      case '\t': return "\\t";
      case '\'': return "\\'";
      case '\"': return "\\\"";
      case '\\': return "\\\\";
    }
    if (c < 32) {
      Debug.Assert(false); return null;
    }
    return c.ToString();
  }

  public override string Emit() {
    return String.Format("'{0}'", Emit(c_));
  }
}

class Location : ValueOrLocation {
  public GValue value_;

  public Location(GValue val) { value_ = val; }
}

class MapNode {
  public readonly MapNode next_;
  public readonly object key_;
  public ValueOrLocation value_;

  public MapNode(object key, ValueOrLocation value, MapNode next) {
    key_ = key; value_ = value; next_ = next;
  }
}

class Map {
  MapNode nodes_;

  protected MapNode Find1(object key) {
    for (MapNode n = nodes_; n != null; n = n.next_)
      if (n.key_ == key)
        return n;
    return null;
  }

  protected virtual MapNode Find(object key) {
    MapNode n = Find1(key);
    Debug.Assert(n != null);
    return n;
  }

  public GValue Get(object key) {
    ValueOrLocation o = Find(key).value_;
    Location loc = o as Location;
    return loc != null ? loc.value_ : (GValue) o;
  }

  public void Set(object key, GValue val) {
    MapNode n = Find(key);
    Location loc = n.value_ as Location;
    if (loc != null)
      loc.value_ = val;
    else n.value_ = val;
  }

  public void Add(object key, ValueOrLocation val) {
    nodes_ = new MapNode(key, val, nodes_);
  }

  public Location GetLocation(object key) {
    MapNode n = Find(key);
    ValueOrLocation o = n.value_;
    Location loc = o as Location;
    if (loc != null)
      return loc;
    loc = new Location((GValue) o);
    n.value_ = loc;
    return loc;
  }
}

class GObject : GValue {
  public readonly Class class_;   // the class of this object

  Map map_ = new Map();

  public GObject(Class cl) {
    class_ = cl;
    while (cl != null) {
      foreach (Field f in cl.fields_)
        map_.Add(f, f.Type().DefaultValue());
      cl = cl.Parent();
    }
  }

  // the type of this object
  public override GType Type() { return class_; }

  public override GValue Get(Field field) { return map_.Get(field); }
  public void Set(Field field, GValue val) { map_.Set(field, val); }
  public Location GetLocation(Field field) { return map_.GetLocation(field); }

  public override int DefaultHashCode() { return base.GetHashCode(); }
  public override int GetHashCode() {
    GInt i = (GInt) Invocation.InvokeMethod(this, GObject.type_.get_hash_code_, new ArrayList(), true);
    return i.i_;
  }

  public override bool DefaultEquals(object o) { return base.Equals(o); }
  public override bool Equals(object o) {
    GValue v = o as GValue;
    if (v == null)
      return false;

    ArrayList args = new ArrayList();
    args.Add(v);
    GBool b = (GBool) Invocation.InvokeMethod(this, GObject.type_.equals_, args, true);
    return b.b_;
  }

  public override string DefaultToString() { return String.Format("<object {0}>", class_.name_); }
  public override string ToString() {
    GString s = (GString) Invocation.InvokeMethod(this, GObject.type_.to_string_, new ArrayList(), true);
    return s.s_;
  }

  // the Object class
  public static readonly ObjectClass type_ = new ObjectClass();
}

class NullType : GType {
  public override string ToString() { return "null_type"; }

  public override SimpleValue DefaultValue() { Debug.Assert(false); return null; }

  public override bool IsSubtype(GType t1) {
    return t1.IsReference();
  }

  protected override bool CanConvertOwnership(GType t, int context) {
    return true;    // conversion from null type to owning type is okay
  }

  public override string EmitType() { Debug.Assert(false); return null; }
}

class Null : SimpleValue {
  public Null() { }

  public static readonly NullType type_ = new NullType();

  public override GType Type() { return type_; }

  public override bool Equals(object o) {
    return o is Null;
  }

  public override int GetHashCode() { return 0; }

  public static readonly Null Instance = new Null();

  public override string ToString() { return ""; }

  public override string Emit() { return "0"; }
}

class Owning : GType {
  GType base_type_;

  public Owning(GType type) {
    base_type_ = type;
  }

  public override Class Parent() { Debug.Assert(false); return null; }

  public override SimpleValue DefaultValue() { return Null.Instance; }

  public override GType BaseType() { return base_type_; }

  public override TypeSet VarDestroys() { return base_type_.TypeDestroys(); }

  public override void FindVarDestroys(int marker, TypeSet set) {
    base_type_.FindTypeDestroys(marker, set);
  }

  public override bool Equals(object o) {
    Owning t = o as Owning;
    return t != null && t.base_type_.Equals(base_type_);
  }

  public override int GetHashCode() { return base_type_.GetHashCode() * 87; }

  public override string ToString() { return base_type_.ToString() + " ^"; }

  public override string EmitTypeName() {
    return base_type_.EmitTypeName();
  }

  string EmitType(string name) {
    return ConstructType(base_type_ == GObject.type_ ? "_OwnRef" : "_Own", name);
  }

  public override string EmitType() {
    // We represent an object ^ using a _OwnRef<Object>.
    // We represent a Foo ^ using an _Own<Foo>.
    return EmitType(EmitTypeName());
  }

  public override string EmitGenericType() {
    // We represent an object ^ [] using an _Array<_OwnRef<Object>>.
    // We represent a Foo ^ [] using an _Array<_Own<Object>>.
    return EmitType("Object");
  }
}

class OwningExpr : TypeExpr {
  readonly TypeExpr expr_;

  public OwningExpr(TypeExpr expr) { expr_ = expr; }

  public override GType Resolve(Program program) {
    GType t = expr_.Resolve(program);
    if (t == null)
        return null;
    if (t.IsValue()) {
      Error("^ cannot be applied to primitive types or strings");
      return null;
    }
    return new Owning(t);
  }
}

class GString : SimpleValue {
  public readonly string s_;

  public GString(string s) { s_ = s; }

  public static readonly StringClass type_ = new StringClass();

  public override GType Type() { return type_; }

  public override bool Equals(object o) {
    GString s = o as GString;
    return s != null && s.s_ == s_;
  }

  public override int GetHashCode() { return s_.GetHashCode(); }

  public override string ToString() { return s_; }

  public override GValue Invoke(Method m, ValueList args) {
    if (m.GetClass() != type_)
      return base.Invoke(m, args);
    switch (m.name_) {
      case "StartsWith": return new GBool(s_.StartsWith(args.GetString()));
      case "EndsWith": return new GBool(s_.EndsWith(args.GetString()));
      default: Debug.Assert(false); return null;
    }
  }

  public static string EmitStringConst(string s) {
    return String.Format("L{0}", EmitString(s));
  }

  public static string EmitString(string s) {
    StringBuilder sb = new StringBuilder();
    sb.Append('"');
    for (int i = 0; i < s.Length; ++i)
      sb.Append(GChar.Emit(s[i]));
    sb.Append('"');
    return sb.ToString();
  }

  public override string Emit() {
    string s = String.Format("new String({0})", EmitStringConst(s_));
    s = String.Format("_Ref<String>({0}).Get()", s);
    return s;
  }
}

class ArrayType : GType {
  GType element_type_;

  public ArrayType(GType type) {
    element_type_ = type;

    // Any type used generically must inherit from Object in C++ code.
    type.BaseType().SetObjectInherit();
  }

  public override bool IsOwned() { return true; }

  public override Class Parent() { return GArray.array_class_; }

  public override SimpleValue DefaultValue() { return Null.Instance; }

  public GType ElementType() { return element_type_; }

  public override bool Equals(object o) {
    ArrayType t = o as ArrayType;
    return (t != null && t.element_type_.Equals(element_type_));
  }

  public override TypeSet TypeDestroys() { return element_type_.VarDestroys(); }

  public override void FindTypeDestroys(int marker, TypeSet set) {
    element_type_.FindVarDestroys(marker, set);
  }

  public override int GetHashCode() { return element_type_.GetHashCode() * 77; }

  public override string EmitTypeName() {
    return ConstructType("_Array", element_type_.EmitGenericType());  
  }

  public override string ToString() {
    return element_type_.ToString() + "[]";
  }
}

class ArrayTypeExpr : TypeExpr {
  readonly TypeExpr expr_;

  public ArrayTypeExpr(TypeExpr expr) { expr_ = expr; }

  public override GType Resolve(Program program) {
    GType t = expr_.Resolve(program);
    return t == null ? null : new ArrayType(t);
  }
}

class GArray : GValue {
  ArrayType type_;

  ValueOrLocation[] elements_;   // each element is a GValue or a Location

  public override GType Type() { return type_; }

  public GArray(ArrayType type, int count) {
    type_ = type;
    elements_ = new ValueOrLocation[count];
    for (int i = 0 ; i < count ; ++i)
      elements_[i] = type_.ElementType().DefaultValue();
  }

  void CheckIndex(int index) {
    if (index < 0 || index >= elements_.Length) {
      Console.WriteLine("error: array access out of bounds");
      Gel.Exit();
    }
  }

  public GValue Get(int index) {
    CheckIndex(index);
    ValueOrLocation o = elements_[index];
    Location loc = o as Location;
    return loc != null ? loc.value_ : (GValue) o;
  }

  public void Set(int index, GValue val) {
    CheckIndex(index);
    Location loc = elements_[index] as Location;
    if (loc != null)
      loc.value_ = val;
    else elements_[index] = val;
  }

  public Location GetLocation(int index) {
    CheckIndex(index);
    ValueOrLocation o = elements_[index];
    Location loc = o as Location;
    if (loc != null)
      return loc;
    loc = new Location((GValue) o);
    elements_[index] = loc;
    return loc;
  }

  public static readonly ArrayClass array_class_ = new ArrayClass();

  public override GValue Invoke(Method m, ValueList args) {
    if (m.GetClass() != array_class_)
      return base.Invoke(m, args);
    switch (m.name_) {
      case "CopyTo":
        GArray a = (GArray) args.Object();
        if (!type_.Equals(a.type_)) {
          Console.WriteLine("can't copy between arrays of different types");
          Gel.Exit();
        }
        // should check for index out of bounds here!
        elements_.CopyTo(a.elements_, args.Int());
        return null;
      case "get_Length":
        return new GInt(elements_.Length);
      default:
        Debug.Assert(false); return null;
    }
  }
}

// A control graph object representing temporary variables destroyed when a top-level
// expression completes evaluation. 
class Temporaries : Node {
  ArrayList /* of GType */ types_ = new ArrayList();

  public void Add(GType t) {
    Debug.Assert(t is Owning);
    types_.Add(t.BaseType());
  }

  public override TypeSet NodeDestroys() {
    TypeSet set = new TypeSet();
    foreach (GType t in types_)
      set.Add(t.TypeDestroys());
    return set;
  }
}

class Context {
  public readonly Program program_;   // containing program
  public readonly Class class_;       // containing class
  public readonly Method method_;     // containing method
  public readonly Escapable escape_;  // containing switch, while, do, for, or foreach
  public readonly Loop loop_;         // containing while, do, for, or foreach
  public Local var_;    // chain of local variable declarations

  // subexpressions of the current top-level expression possibly yielding temporary objects
  ArrayList /* of Expression */ temporaries_ = new ArrayList();

  public Context(Program program) { program_ = program; }

  public Context(Class cl) { program_ = cl.GetProgram(); class_ = cl; }

  // Copy a context; new variable declarations in either copy will not be visible in the other.
  public Context(Context cx) {
    program_ = cx.program_;
    class_ = cx.class_;
    method_ = cx.method_;
    escape_ = cx.escape_;
    loop_ = cx.loop_;
    var_ = cx.var_;
  }

  public Context(Context cx, Class c) : this(cx) { class_ = c; }

  public Context(Context cx, Method m) : this(cx) { method_ = m; }

  public Context(Context cx, Loop l) : this(cx) {
    escape_ = l;
    loop_ = l;
  }

  public Context(Context cx, Switch s) : this(cx) { escape_ = s; }

  public bool IsStatic() { return method_ == null || method_.IsStatic(); }

  public void SetVar(Local var) {
    var_ = var;
  }

  public Local FindVar(string name) {
    for (Local v = var_; v != null; v = v.next_)
      if (v.name_ == name)
        return v;
    return null;
  }

  public Control Prev() { return program_.prev_; }

  public void SetPrev(Control c) {
    Debug.Assert(c != null);
    program_.prev_ = c;
  }

  public void ClearPrev() { program_.prev_ = null; }

  // Called when we begin type-checking a top-level expression.
  public void EnterExpression() {
    Debug.Assert(temporaries_.Count == 0);
  }

  public void AddTemporary(Expression e) {
    temporaries_.Add(e);
  }

  // Called when we've finished type-checking a top-level expression.
  public void FinishExpression() {
    Temporaries t = null;
    foreach (Expression e in temporaries_)
      if (!e.LosesOwnership()) {
        if (t == null)
          t = new Temporaries();
        t.Add(e.TemporaryType());
      }
    if (t != null)
      t.AddControl(this);
    temporaries_.Clear();
  }
}

class Env : Map {
  public readonly GValue this_;
  readonly Env next_;

  public Env(GValue _this) { this_ = _this; next_ = null; }
  public Env(Env next) { this_ = next.this_; next_ = next; }

  protected override MapNode Find(object key) {
    for (Env e = this; e != null; e = e.next_) {
      MapNode n = e.Find1(key);
      if (n != null)
        return n;
    }
    Debug.Assert(false);
    return null;
  }

  public static readonly Env static_ = new Env((GValue) null);
}

class TypeLiteral : TypeExpr {
  public readonly GType type_;

  public TypeLiteral(GType type) { type_ = type; }

  public override GType Resolve(Program program) {
    return type_;
  }
}

class TypeName : TypeExpr {
  string name_;

  public TypeName(string name) { name_ = name; }

  public override GType Resolve(Program program) {
    GType type = program.FindClass(name_);
    if (type == null)
      Error("unknown type: {0}", name_);
    return type;
  }
}

abstract class Traverser {
  // Handle a graph item found in a depth first search; returns one of the exit codes below.
  public abstract int Handle(Control control);

  public const int Continue = 0,  // continue searching, including children of this node
                   Cut = 1,       // continue searching, excluding children of this node
                   Abort = 2;     // abort the search

}

// an item in the control graph: either a Node or Joiner
abstract class Control : Syntax {
  public int marker_ = 0;    // marker for depth first search of control flow graph

  static int marker_value_ = 0;
  public static int GetMarkerValue() { return ++marker_value_; }

  // A node representing unreachability.  We could represent this using null, but it's
  // clearer to have this be explicit. 
  public static readonly Node unreachable_ = new Node();

  // A helper function for Traverse.  If we return true then we should cut the
  // traversal at this point.
  protected bool Visit(Traverser traverser, int marker, out bool ok) {
    ok = true;
    if (marker_ == marker)
      return true;  // we've already seen this node
    marker_ = marker;
    int code = traverser.Handle(this);
    if (code == Traverser.Abort)
      ok = false;
    return (code != Traverser.Continue);
  }

  // Perform a depth-first search of the graph starting with this item, calling the
  // given Traverser for each Node found.  Returns false if the search was aborted.
  public abstract bool Traverse(Traverser traverser, int marker);
}

// A node in the control graph.
class Node : Control {
  public Control prev_;   // previous item in graph

  // add this node to the control graph
  public bool AddControl(Context ctx) {
    Control prev = ctx.Prev();
    Debug.Assert(prev != null);
    if (prev != unreachable_) {   // we're in a live code path
      prev_ = prev;
      ctx.SetPrev(this);
      return true;
    }
    else return false;
  }

  // If this node calls a method or constructor then return it; otherwise return null.
  // implementers: LValue, Invocation, New, Assign, Constructor
  public virtual Method Calls() { return null; }

  // Return the set of types which this node may destroy.
  // implementers: Assign, RefOutArgument, Scoped, Temporaries
  public virtual TypeSet NodeDestroys() { return TypeSet.empty_; }

  // Return true if this node assigns a value to the given Local.
  // implementers: Assign, RefOutArgument, VariableDeclaration, Method, ForEach
  public virtual bool Sets(Local local) { return false; }

  // Return true if this node takes ownership from the given local.
  // implementers: Name
  public virtual bool Takes(Local local) { return false; }

  public bool CanDestroy(GType type) {
    Method m = Calls();
    if (m != null && m.Destroys().Contains(type))
      return true;
    return NodeDestroys().Contains(type);
  }

  public override bool Traverse(Traverser traverser, int marker) {
    Node n = this;
    Control prev = null;

    // Traverse nodes iteratively until we come to a Join; this efficiently avoids
    // making a recursive call on each successive node.
    while (n != null) {
      bool ok;
      if (n.Visit(traverser, marker, out ok))
        return ok;
      prev = n.prev_;
      n = prev as Node;
    }
    Debug.Assert(prev != null);  // a Traverser must end traversal before we run off the end
    return prev.Traverse(traverser, marker);
  }
}

class Joiner : Control {
  ArrayList /* of Control */ prev_ = new ArrayList();

  public void Join(Control c) {
    Debug.Assert(c != null);
    if (c == unreachable_)
      return;
    if (prev_.Count == 0 || c != prev_[0])
      prev_.Add(c);
  }

  // add this joiner to the control graph
  public void AddControl(Context ctx) {
    Control prev = ctx.Prev();
    Debug.Assert(prev != null);
    if (prev != unreachable_) {   // we're in a live code path
      Join(prev);
      ctx.SetPrev(this);
    }
  }

  // Once we've finished merging paths into a Joiner, if the Joiner points to only a single path
  // then as an optimization we can discard the Joiner and just use that path instead.
  public Control Combine() {
    ArrayList p = prev_;
    switch (p.Count) {
      case 0:
        prev_ = null;
        return unreachable_;
      case 1:
        prev_ = null;
        return (Control) p[0];
      default: return this;
    }
  }

  public override bool Traverse(Traverser traverser, int marker) {
    bool ok;
    if (Visit(traverser, marker, out ok))
      return ok;

    foreach (Control p in prev_)
      if (!p.Traverse(traverser, marker))
        return false;  // aborted
    return true;
  }
}

class ExprKind {
  public const int Value = 0,

  // either a local variable, or an expression whose value is derived from local variables
                   Local = 1,

                   Field = 2,
                   Property = 3,
                   Indexer = 4,
                   Type = 5;
}

class SourceWriter {
  public readonly StreamWriter writer_;
  int indent_ = 0;

  public SourceWriter(StreamWriter writer) { writer_ = writer; }

  public void AddIndent() { ++indent_; }
  public void SubIndent() { --indent_; }

  public void Indent(int adjust) {
    for (int i = 0; i < 2 * indent_ + adjust; ++i)
      writer_.Write(' ');
  }

  public void Indent() { Indent(0); }

  public void Write(string s) { writer_.Write(s); }
  public void Write(string s, object arg) { writer_.Write(s, arg); }
  public void Write(string s, object arg1, object arg2) { writer_.Write(s, arg1, arg2); }
  public void Write(string s, object arg1, object arg2, object arg3) { writer_.Write(s, arg1, arg2, arg3); }

  public void IWrite(string s) { Indent(); Write(s); }
  public void IWrite(string s, object arg) { Indent(); Write(s, arg); }
  public void IWrite(string s, object arg1, object arg2) { Indent(); Write(s, arg1, arg2); }
  public void IWrite(string s, object arg1, object arg2, object arg3) { Indent(); Write(s, arg1, arg2, arg3); }

  public void WriteLine(string s) { writer_.WriteLine(s); }
  public void WriteLine(string s, object arg) { writer_.WriteLine(s, arg); }
  public void WriteLine(string s, object arg1, object arg2) { writer_.WriteLine(s, arg1, arg2); }
  public void WriteLine(string s, object arg1, object arg2, object arg3) { writer_.WriteLine(s, arg1, arg2, arg3); }

  public void IWriteLine(string s) { Indent(); WriteLine(s); }
  public void IWriteLine(string s, object arg) { Indent(); WriteLine(s, arg); }
  public void IWriteLine(string s, object arg1, object arg2) { Indent(); WriteLine(s, arg1, arg2); }
  public void IWriteLine(string s, object arg1, object arg2, object arg3) { Indent(); WriteLine(s, arg1, arg2, arg3); }

  public void OpenBrace() { WriteLine("{"); AddIndent(); }
  public void CloseBrace() { SubIndent(); IWriteLine("}"); }
}

class Usage {
  public const int Used = 0, LosesOwnership = 1, Unused = 2;
}

abstract class Expression : Node {
  protected int usage_ = Usage.Used;

  // start_ and end_ mark the points in the control graph where this expression is evaluated and used,
  // respectively.  If this expression's type is owned, we use this information to determine whether
  // we need to emit a reference count for this expression.
  Control start_;
  Control end_;

  public abstract GType Check(Context ctx);

  public GType CheckTop(Context ctx) {
    ctx.EnterExpression();
    GType t = Check(ctx);
    ctx.FinishExpression();
    return t;
  }

  public GType CheckAndHold(Context ctx) {
    GType t = Check(ctx);
    if (t != null)
      HoldRef(ctx);
    return t;
  }

  // A fancier checking protocol implemented by lvalues and used by certain callers.
  public virtual GType Check(Context ctx, bool read, bool write, bool type_ok) {
    return Check(ctx);
  }

  public virtual int Kind() { return ExprKind.Value; }
  public virtual bool IsRefOutParameter() { return false; }

  public virtual bool IsTrueLiteral() { return false; }
  public virtual bool IsFalseLiteral() { return false; }

  // If this expression is a local variable (possibly cast to a different type),
  // then return that local; otherwise return null.
  public virtual Local GetLocal() { return null; }

  // Return the type of an expression yielding a temporary object.
  // (It might be convenient to generalize this to be able to return any expression's type.)
  public virtual GType TemporaryType() { Debug.Assert(false); return null; }

  public void HoldRef(Context ctx) {
    start_ = ctx.Prev();
    Debug.Assert(start_ != null);
  }

  public void ReleaseRef(Context ctx) {
    end_ = ctx.Prev();
    Debug.Assert(end_ != null);
  }

  public abstract GValue Eval(Env env);

  public bool Check(Context ctx, GType t2) {
    GType t = Check(ctx);
    return t == null ? false : t.CheckConvert(this, t2);
  }

  public bool CheckTop(Context ctx, GType t2) {
    GType t = CheckTop(ctx);
    return t == null ? false : t.CheckConvert(this, t2);
  }

  public GValue Eval(Env env, GType t) { return Eval(env).Convert(t); }
  public bool EvalBool(Env env) { return ((GBool) Eval(env)).b_; }
  public int EvalInt(Env env) { return ((GInt) Eval(env, GInt.type_)).i_; }
  public double EvalDouble(Env env) { return ((GDouble) Eval(env, GDouble.type_)).d_; }
  public float EvalFloat(Env env) { return ((GFloat) Eval(env, GFloat.type_)).f_; }
  public string EvalString(Env env) { return ((GString) Eval(env)).s_; }

  public virtual bool IsConstant() {
    return false;
  }

  public bool CheckConstant() {
    if (IsConstant())
      return true;
    Error("expression must be a constant");
    return false;
  }

  public bool LosesOwnership() { return usage_ == Usage.LosesOwnership; }

  public virtual void LoseOwnership() {
    usage_ = Usage.LosesOwnership;
  }

  public void SetUnused() {
    Debug.Assert(usage_ == Usage.Used);
    usage_ = Usage.Unused;
  }

  protected static string EmitAllocate(string type, string args, bool lose_ownership) {
    // If an allocated object never loses ownership then it can be stack allocated.  We
    // can't emit "&Foo(...)" since standard C++ doesn't allow us to take the address
    // of a temporary (both GCC and Visual C++ will allow this, but will emit a warning).
    // So instead we call a function _Addr() to get the address.
    return String.Format(
      !lose_ownership ? "{0}({1})._Addr<{0} *>()" : "new {0}({1})",
      type, args);
  }

  public void CheckLoseOwnership(GType from, GType to) {
    if (to is Owning)
      LoseOwnership();
  }

  public abstract string Emit();    // return a C++ expression representing this GEL2 expression

  public bool NeedsRef(GType type) {
    return Gel.program_.safe_ && type.IsOwned() &&
      (Gel.always_ref_ || ExpressionTraverser.NeedRef(start_, end_, this, type));
  }

  // Emit this expression, adding a reference-counting _Ptr wrapper if needed.
  public string EmitRef(GType type) {
    string s = Emit();
    if (NeedsRef(type))
      s = String.Format("{0}({1}).Get()", type.EmitType(), s);
    return s;
  }

  // Emit an implicit conversion from type [source] to type [dest].
  public static string EmitImplicit(GType source, GType dest, string val, bool loses_ownership) {
    source = source.BaseType();
    dest = dest.BaseType();

    if (!source.IsReference() && dest == GObject.type_) {   // a boxing conversion
      Class c = (Class) source;
      return EmitAllocate(c.name_, val, loses_ownership);
    }

    if (source == GInt.type_ && dest == GFloat.type_)
      // an implicit conversion in GEL2, but C++ compilers may warn if we don't use a cast
      return String.Format("static_cast<float>({0})", val);
    
    return val;
  }

  // Emit this expression, converting implicitly from type [source] to type [dest].
  public string Emit(GType source, GType dest) {
    return EmitImplicit(source, dest, Emit(), LosesOwnership());
  }

  public string EmitRef(GType source, GType dest) {
    return EmitImplicit(source, dest, EmitRef(source), LosesOwnership());
  }

  public static string EmitExplicit(GType source, GType dest, string val, bool loses_ownership) {
    source = source.BaseType();
    dest = dest.BaseType();

    if (source.CanConvert(dest)) {
      string v = EmitImplicit(source, dest, val, loses_ownership);
      if (v != val)
        return v;

      // There's nothing to do, but we emit a static_cast anyway since a GEL2 programmer
      // may use a type cast for method call disambiguation; we want to pass such casts onward.
      return String.Format("static_cast<{0} >({1})", dest.EmitExprType(), val);
    }

    if (source.IsReference() && dest.IsReference()) {
      string s = String.Format("_Cast<{0} *>({1})", dest.EmitTypeName(), val);
      ArrayType at = dest as ArrayType;
      if (at != null) {
        GType element_type = at.ElementType();
        if (element_type.IsReference())
          s = String.Format("({0})->CheckType(&typeid({1}))", s, element_type.EmitType());
      }
      return s;
    }

    if (source == GObject.type_ && !dest.IsReference())  // unboxing conversion
      return String.Format("_Unbox<{0} *>({1})->Value()", ((Class) dest).name_, val);

    if (!source.IsReference() && !dest.IsReference())
      return String.Format("static_cast<{0}>({1})", dest.EmitType(), val);

    Debug.Assert(false);
    return null;
  }

  public string EmitExplicit(GType source, GType dest) {
    return EmitExplicit(source, dest, Emit(), LosesOwnership());
  }

  public virtual string EmitArrow(GType t, Member m) {
    return String.Format("({0})->", EmitRef(t));
  }

  // Emit this expression as a local or field initializer, implicitly casting to [type].
  public void EmitAsInitializer(SourceWriter w, GType initializer_type, GType type) {
    // For owning variables, we must emit an initializer of the form "Foo var(val)" since
    // _Own has no copy constructor and hence GCC won't allow "Foo var = val".  For other
    // variables either form is valid, but we emit the second form since it looks clearer.
    w.Write(type is Owning ? "({0})" : " = {0}", Emit(initializer_type, type));
  }

  // Given a value retrieved from a variable, emit an accessor call if needed.
  protected string OwnSuffix(GType t) {
    if (t is Owning)
      return LosesOwnership() ? ".Take()" : ".Get()";
    if (t == GString.type_ || Gel.program_.safe_ && t.IsReference() )
      return ".Get()";
    return "";
  }

  // Given a value returned from a function call, emit a wrapper and/or accessor call if needed.
  protected string Hold(GType t, string s) {
    if (t == GString.type_)
      return s + ".Get()";
    if (t is Owning)
      switch (usage_) {
        case Usage.Used:  // does not lose ownership; we need a freeing wrapper
          return String.Format("{0}({1}).Get()", t.EmitType(), s);
        case Usage.LosesOwnership:
          return s;
        case Usage.Unused:  // a top-level owning expression
          return "delete " + s;
        default: Debug.Assert(false); break;
      }
    return s;
  }
}

class Literal : Expression {
  public readonly SimpleValue value_;

  public Literal(SimpleValue v) { value_ = v; }

  public override bool IsTrueLiteral() {
    GBool b = value_ as GBool;
    return b != null && b.b_;
  }

  public override bool IsFalseLiteral() {
    GBool b = value_ as GBool;
    return b != null && !b.b_;
  }

  public override GType Check(Context ctx) { return value_.Type(); }

  public override GValue Eval(Env env) { return value_; }

  public override bool IsConstant() { return true; }

  public override string Emit() { return value_.Emit(); }
}

// An LValue is an expression which can be assigned to: a Name, Dot, or Sub.
//
// In the control graph, an LValue represents a read; if an LValue is written then
// some other node (e.g. an Assign) will appear representing the write.
abstract class LValue : Expression {
  public override GType Check(Context ctx) {
    return Check(ctx, true, false, false);
  }

  public abstract override GType Check(Context ctx, bool read, bool write, bool type_ok);

  public abstract GType StorageType();

  public virtual bool IsLocal() { return false; }
  public virtual bool IsLocal(Local l) { return false; }

  public abstract PropertyOrIndexer GetPropertyOrIndexer();

  public override Method Calls() {
    PropertyOrIndexer pi = GetPropertyOrIndexer();
    return pi == null ? null : pi.Getter();
  }

  public virtual void ReleaseAll(Context ctx) { }

  // For LValues, evaluation proceeds in two stages: the caller first calls Eval1(), then
  // passes the resulting values to EvalGet(), EvalSet() and/or EvalLocation().  This lets
  // us evaluate the left side of an assignment expression before evaluating the right side,
  // and also lets us get and then set an indexer without evaluating its expression twice.

  public abstract void Eval1(Env env, out GValue v1, out GValue v2);
  public abstract GValue EvalGet(Env env, GValue v1, GValue v2);
  public abstract void EvalSet(Env env, GValue v1, GValue v2, GValue val);
  public abstract Location EvalLocation(Env env, GValue v1, GValue v2);

  public override GValue Eval(Env env) {
    GValue val1, val2;
    Eval1(env, out val1, out val2);
    return EvalGet(env, val1, val2);
  }

  public void EvalSet(Env env, GValue v) {
    GValue val1, val2;
    Eval1(env, out val1, out val2);
    EvalSet(env, val1, val2, v);
  }

  public Location EvalLocation(Env env) {
    GValue val1, val2;
    Eval1(env, out val1, out val2);
    return EvalLocation(env, val1, val2);
  }

  public abstract string EmitSet(string val);

  public abstract string EmitLocation();
}

class Name : LValue {
  public readonly string name_;

  protected Local local_;
  protected LMember field_;  // a field or property

  public Name(string n) { name_ = n; }

  public override bool IsLocal() { return local_ != null; }
  public override bool IsLocal(Local l) { return local_ == l; }
  public override bool IsRefOutParameter() { return local_ is RefOutParameter; }

  public override bool IsConstant() {
    Debug.Assert(local_ != null || field_ != null);   // make sure we were already type checked
    return field_ is ConstField;
  }

  public override GType Check(Context ctx, bool read, bool write, bool type_ok) {
    local_ = ctx.FindVar(name_);
    if (local_ != null) {
      // For reads, we add this Name node to the flow graph; for writes,
      // the caller must add a node which defines this Name.
      if (read)
        if (AddControl(ctx))
          local_.AddUse(this);
      if (write)
        local_.SetMutable();
      return local_.ReadType();
    }

    GType cl = type_ok ? ctx.program_.FindClass(name_) : null;
    field_ = (LMember) ctx.class_.Lookup(this, ctx.class_, false, MemberKind.Field, name_, null, cl == null);
    if (field_ != null) {
      if (!field_.CheckAccess(this, ctx, write, true, !ctx.IsStatic()))
        return null;
      if (read) {
        // Only property reads have side effects we need to register in the control graph.
        if (field_ is Property)
          AddControl(ctx);
      }
      return field_.Type().BaseType();
    }

    return cl;
  }

  public override int Kind() {
    if (local_ != null)
      return ExprKind.Local;
    if (field_ is Field)
      return ExprKind.Field;
    if (field_ is Property)
      return ExprKind.Property;
    Debug.Assert(field_ == null);
    return ExprKind.Type;
  }

  public override Local GetLocal() { return local_; }

  public override GType StorageType() { return local_ != null ? local_.Type() : field_.Type(); }

  public override bool Takes(Local local) {
    return local_ == local && LosesOwnership() && local.Type() is Owning;
  }

  public override PropertyOrIndexer GetPropertyOrIndexer() { return field_ as Property; }

  public override void Eval1(Env env, out GValue v1, out GValue v2) { v1 = v2 = null; }

  public override GValue EvalGet(Env env, GValue v1, GValue v2) {
    return local_ != null ? env.Get(local_) : field_.Get(env.this_);
  }

  public override void EvalSet(Env env, GValue v1, GValue v2, GValue val) {
    if (local_ != null)
      env.Set(local_, val);
    else field_.Set((GObject) env.this_, val);
  }

  public override Location EvalLocation(Env env, GValue v1, GValue v2) {
    return local_ != null ? env.GetLocation(local_) : field_.GetLocation((GObject) env.this_);
  }

  public override string Emit() {
    if (local_ != null) {
      string s = local_.Emit();
      return local_.IsWrapper() ? s + OwnSuffix(local_.Type()) : s;
    }
    if (field_ != null)
      return field_.Emit() + OwnSuffix(field_.Type());
    return name_;
  }

  public override string EmitSet(string val) {
    if (local_ != null)
      return String.Format("{0} = {1}", local_.Emit(), val);
    Debug.Assert(field_ != null);
    return field_.EmitSet(val);
  }

  public override string EmitLocation() {
    if (local_ != null)
      return "&" + local_.Emit();
    if (field_ != null)
      return "&" + field_.Emit();
    return "&" + name_;
  }
}

class Parenthesized : Expression {
  Expression expr_;

  public Parenthesized(Expression e) { expr_ = e; }

  public override GType Check(Context ctx) { return expr_.Check(ctx); }

  public override void LoseOwnership() {
    base.LoseOwnership();
    expr_.LoseOwnership();
  }

  public override GValue Eval(Env env) { return expr_.Eval(env); }

  public override string Emit() { return String.Format("({0})", expr_.Emit()); }
}

class PredefinedType : Expression {
  Class type_;

  public PredefinedType(Class type) { type_ = type; }

  public override GType Check(Context ctx) { Debug.Assert(false); return null; }

  public override GType Check(Context ctx, bool read, bool write, bool type_ok) {
    Debug.Assert(type_ok);
    return type_;
  }

  public override int Kind() {
    return ExprKind.Type;
  }

  public override GValue Eval(Env env) { Debug.Assert(false); return null; }

  public override string Emit() { return type_.name_; }
}

class Dot : LValue {
  Expression expr_;  // set to null for a static invocation
  GType expr_type_;
  string name_;

  LMember field_;

  public Dot(Expression expr, string name) { expr_ = expr; name_ = name; }

  public override bool IsConstant() {
    Debug.Assert(field_ != null);   // make sure we were already type checked
    return field_ is ConstField;
  }

  public override GType Check(Context ctx, bool read, bool write, bool type_ok) {
    expr_type_ = expr_.Check(ctx, true, false, true);
    if (expr_type_ == null)
      return null;
    bool is_static = (expr_.Kind() == ExprKind.Type);
    if (is_static)
      expr_ = null;
    else expr_.HoldRef(ctx);

    field_ = (LMember) expr_type_.Lookup(this, ctx.class_, expr_ is Base,
                                         MemberKind.Field, name_, null, true);
    if (field_ == null)
      return null;

    if (!field_.CheckAccess(this, ctx, write, is_static, !is_static))
      return null;

    if (read) {
      // Only property reads have side effects we need to register in the control graph.
      // (For writes the caller, such as Assign, will add its own node.)
      if (field_ is Property)
        AddControl(ctx);
      if (expr_ != null)
        expr_.ReleaseRef(ctx);
    }

    return field_.Type().BaseType();
  }

  public override void ReleaseAll(Context ctx) {
    if (expr_ != null)
      expr_.ReleaseRef(ctx);
  }

  public override int Kind() {
    if (field_ is Field)
      return ExprKind.Field;
    if (field_ is Property)
      return ExprKind.Property;
    Debug.Assert(false);
    return 0;
  }

  public override GType StorageType() { return field_.Type(); }

  public override PropertyOrIndexer GetPropertyOrIndexer() { return field_ as Property; }

  public override void Eval1(Env env, out GValue v1, out GValue v2) {
    v1 = v2 = null;
    if (expr_ != null) {  // calling instance method
      v1 = expr_.Eval(env);
      if (v1 is Null) {
        Error("attempted to access field of null object");
        Gel.Exit();
      }
    }
  }

  public override GValue EvalGet(Env env, GValue v1, GValue v2) {
    return field_.Get(v1);
  }

  public override void EvalSet(Env env, GValue v1, GValue v2, GValue val) {
    field_.Set((GObject) v1, val);
  }

  public override Location EvalLocation(Env env, GValue v1, GValue v2) {
    return field_.GetLocation((GObject) v1);
  }

  string EmitPrefix() {
    return expr_ != null ?
      expr_.EmitArrow(expr_type_, field_) :  // instance access
      field_.GetClass().name_ + "::";   // static access
  }

  public override string Emit() {
    string s = EmitPrefix() + field_.Emit();
    GType t = field_.Type();
    return field_ is Property ? Hold(t, s) : s + OwnSuffix(t);
  }

  public override string EmitSet(string val) {
    return EmitPrefix() + field_.EmitSet(val);
  }

  public override string EmitLocation() {
    return "&" + EmitPrefix() + field_.Emit();
  }
}

class Mode {
  public const int In = 0,
                   Ref = 1,
                   Out = 2;

  public static string ToString(int mode) {
    switch (mode) {
      case In: return "";
      case Ref: return "ref ";
      case Out: return "out ";
      default: Debug.Assert(false); return null;
    }
  }
}

abstract class Argument : Node {
  protected GType type_;

  public GType Type() { return type_; }

  public abstract int GetMode();
  public string TypeString() { return Mode.ToString(GetMode()) + type_.ToString(); }

  public abstract bool Check(Context ctx);
  public abstract void FinishCall(Context ctx);

  public abstract ValueOrLocation Eval(Env env, GType t);

  public abstract string Emit(GType t);
}

class InArgument : Argument {
  public readonly Expression expr_;

  public InArgument(Expression expr) { expr_ = expr; }
  public InArgument(GType type) { type_ = type; }

  public override int GetMode() { return 0; }

  public override bool Check(Context ctx) {
    if (type_ == null)
      type_ = expr_.CheckAndHold(ctx);
    return type_ != null;
  }

  public override void FinishCall(Context ctx) {
    expr_.ReleaseRef(ctx);
  }

  public override ValueOrLocation Eval(Env env, GType t) { return expr_.Eval(env, t); }

  public override string Emit(GType t) {
    return expr_.EmitRef(type_, t);
  }
}

class RefOutArgument : Argument {
  public readonly int mode_;
  public readonly LValue lvalue_;

  public RefOutArgument(int mode, LValue lvalue) { mode_ = mode; lvalue_ = lvalue; }

  public override int GetMode() { return mode_; }

  public override bool Check(Context ctx) {
    type_ = lvalue_.Check(ctx, mode_ == Mode.Ref, true, false);
    if (type_ == null)
      return false;
    if (lvalue_.Kind() == ExprKind.Indexer) {
      Error("can't pass indexer as ref or out argument");
      return false;
    }
    return true;
  }

  public override void FinishCall(Context ctx) {
    AddControl(ctx);
  }

  public override bool Sets(Local local) { return lvalue_.IsLocal(local); }

  public override TypeSet NodeDestroys() { return lvalue_.StorageType().VarDestroys(); }

  public GType StorageType() { return lvalue_.StorageType(); }

  public override ValueOrLocation Eval(Env env, GType t) { return lvalue_.EvalLocation(env); }

  public override string Emit(GType t) { return lvalue_.EmitLocation(); }
}

class Invocation : Expression {
  Expression obj_;    // may be null
  GType obj_type_;
  string name_;
  ArrayList /* of Argument */ arguments_;

  Method method_;

  public Invocation(Expression obj, string name, ArrayList arguments) {
    obj_ = obj; name_ = name; arguments_ = arguments;
  }

  public static Method CheckInvoke(Node caller, Context ctx, bool through_base, GType type,
                                   string name, ArrayList /* of Argument */ arguments,
                                   int kind) {
    foreach (Argument arg in arguments)
      if (!arg.Check(ctx))
        return null;

    // Add the caller to the graph; this node represents the invocation itself.
    caller.AddControl(ctx);

    // Now we can release temporary expressions and add control graph nodes representing
    // assignments to ref and out parameters.
    foreach (Argument arg in arguments)
      arg.FinishCall(ctx);

    Method m = (Method) type.Lookup(caller, ctx.class_, through_base, kind, name, arguments, true);
    if (m != null)
      for (int i = 0; i < m.parameters_.Count; ++i) {
        Parameter p = m.Param(i);
        if (p.GetMode() == Mode.In) {
          InArgument a = (InArgument) arguments[i];
          a.expr_.CheckLoseOwnership(a.Type(), p.Type());
        }
      }
    return m;
  }

  public override GType Check(Context ctx) {
    GType t;
    bool is_class = false;  // true when calling a static method using a class name

    if (obj_ == null)
      t = ctx.class_;
    else {
      t = obj_type_ = obj_.Check(ctx, true, false, true);
      if (t == null)
        return null;
      if (obj_.Kind() == ExprKind.Type)
        is_class = true;
      else obj_.HoldRef(ctx);
    }

    method_ = CheckInvoke(this, ctx, obj_ is Base, t, name_, arguments_, MemberKind.Method);
    if (method_ == null)
      return null;

    if (method_ is Constructor) {
      Error("can't call constructor directly");
      return null;
    }

    if (obj_ == null) {
      if (!method_.IsStatic() && ctx.IsStatic()) {
        Error("can't call instance method in static context");
        return null;
      }
    } else {
      obj_.ReleaseRef(ctx);
      if (is_class) {
        if (!method_.IsStatic()) {
          Error("can't invoke non-static method through class name");
          return null;
        }
      } else {
        if (method_.IsStatic()) {
          Error("can't invoke static method through object");
          return null;
        }
      }
    }

    GType ret = method_.ReturnType();
    if (ret is Owning)
      ctx.AddTemporary(this);
    return ret;
  }

  public override GType TemporaryType() { return method_.ReturnType(); }

  public override Method Calls() { return method_; }

  public static GValue InvokeMethod(GValue obj, Method m, ArrayList /* of GValue */ values,
                                    bool virtual_ok) {
    if (m.IsVirtual() && virtual_ok) {
      GType t = obj.Type();
      m = (Method) t.FindMatchingMember(m, true);
      Debug.Assert(m != null);
    }
    return m.Invoke(obj, values);
  }

  public static GValue CallMethod(Env env, GValue obj,
                                  Method m, ArrayList /* of Argument */ args,
                                  bool virtual_ok) {
    ArrayList /* of ValueOrLocation */ values = new ArrayList();
    int i;
    for (i = 0 ; i < args.Count ; ++i) {
      Argument a = (Argument) args[i];
      values.Add(a.Eval(env, m.Param(i).Type()));
    }
    return InvokeMethod(obj, m, values, virtual_ok);
  }

  public GValue Eval(Env env, Expression obj, Method m, ArrayList /* of Argument */ args) {
    GValue v;
    if (m.IsStatic())
      v = null;
    else {
      if (obj == null)
        v = env.this_;
      else {
        v = obj.Eval(env);
        if (v is Null) {
          Error("attempted to call method on null object");
          Gel.Exit();
        }
      }
    }

    return CallMethod(env, v, m, args, !(obj is Base));
  }

  public override GValue Eval(Env env) {
    return Eval(env, obj_, method_, arguments_);
  }

  public static string EmitArguments(Method m, ArrayList /* of Argument */ arguments) {
    StringBuilder sb = new StringBuilder();
    Debug.Assert(m.parameters_.Count == arguments.Count);
    for (int i = 0; i < arguments.Count; ++i) {
      if (i > 0)
        sb.Append(", ");
      Argument a = (Argument)arguments[i];
      sb.Append(a.Emit(m.Param(i).Type()));
    }
    return sb.ToString();
  }

  public override string Emit() {
    StringBuilder sb = new StringBuilder();
    if (obj_ != null) {
      if (method_.IsStatic())
        sb.AppendFormat("{0}::", obj_.Emit());
      else if (obj_type_.IsReference())
        sb.Append(obj_.EmitArrow(obj_type_, method_));
      else sb.AppendFormat("({0})->", obj_.Emit(obj_type_, GObject.type_));   // box values
    }
    sb.AppendFormat("{0} ({1})", method_.name_, EmitArguments(method_, arguments_));
    return Hold(method_.ReturnType(), sb.ToString());
  }
}

// an expression of the form a[b]
class Sub : LValue {
  readonly Expression base_;
  GType base_type_;
  readonly Expression index_;
  GType index_type_;

  GType element_type_;    // for array accesses; null for indexers
  Indexer indexer_;

  public Sub(Expression base_exp, Expression index) { base_ = base_exp; index_ = index; }

  public override GType Check(Context ctx, bool read, bool write, bool type_ok) {
    base_type_ = base_.CheckAndHold(ctx);
    if (base_type_ == null)
      return null;

    index_type_ = index_.CheckAndHold(ctx);
    if (index_type_ == null)
      return null;

    if (read)
      ReleaseAll(ctx);

    ArrayType at = base_type_.BaseType() as ArrayType;
    if (at != null) {
      if (!index_type_.CheckConvert(this, GInt.type_))
        return null;
      element_type_ = at.ElementType();
      return element_type_.BaseType();
    }

    ArrayList arguments = new ArrayList();
    arguments.Add(new InArgument(index_type_));

    indexer_ = (Indexer) base_type_.Lookup(this, ctx.class_, base_ is Base,
                                           MemberKind.Indexer, null, arguments, true);
    if (indexer_ == null)
      return null;

    if (read && !indexer_.CheckAssigning(this, ctx, false) ||
        write && !indexer_.CheckAssigning(this, ctx, true))
      return null;

    if (read)
      AddControl(ctx);

    return indexer_.Type();
  }

  public override void ReleaseAll(Context ctx) {
    base_.ReleaseRef(ctx);
    index_.ReleaseRef(ctx);
  }

  public override int Kind() {
    return element_type_ != null ? ExprKind.Field : ExprKind.Indexer;
  }

  public override GType StorageType() {
    return element_type_ != null ? element_type_ : indexer_.Type();
  }

  public override PropertyOrIndexer GetPropertyOrIndexer() { return indexer_; }

  public override void Eval1(Env env, out GValue v1, out GValue v2) {
    v1 = base_.Eval(env);
    if (v1 is Null) {
      Error("attempted array or indexer access through null");
      Gel.Exit();
    }
    v2 = index_.Eval(env);
  }

  int Index(GValue v) {
    return ((GInt) v.Convert(GInt.type_)).i_;
  }

  public override GValue EvalGet(Env env, GValue v1, GValue v2) {
    if (indexer_ == null)
      return ((GArray) v1).Get(Index(v2));
    ArrayList args = new ArrayList();
    args.Add(v2);
    return Invocation.InvokeMethod(v1, indexer_.Getter(), args, true);
  }

  public override void EvalSet(Env env, GValue v1, GValue v2, GValue val) {
    if (indexer_ == null) {
      ((GArray) v1).Set(Index(v2), val);
      return;
    }
    ArrayList args = new ArrayList();
    args.Add(v2);
    args.Add(val);
    Invocation.InvokeMethod(v1, indexer_.Setter(), args, true);
  }

  public override Location EvalLocation(Env env, GValue v1, GValue v2) {
    int i = ((GInt) v2).i_;
    return ((GArray) v1).GetLocation(i);
  }

  string EmitBase() {
    return base_.EmitArrow(base_type_, indexer_);
  }

  string EmitIndex() {
    return index_.Emit(index_type_, indexer_ != null ? indexer_.parameter_.Type() : GInt.type_);
  }

  public override string Emit() {
    string get = String.Format("{0}get_Item({1})", EmitBase(), EmitIndex());
    if (element_type_ != null) {
      get = get + OwnSuffix(element_type_);
      if (!element_type_.IsValue())
        get = String.Format("static_cast<{0} >({1})", element_type_.EmitExprType(), get);
      return get;
    } else return Hold(indexer_.Type(), get);
  }

  public override string EmitSet(string val) {
    return String.Format(element_type_ != null ? "{0}get_Item({1}) = {2}" : "{0}set_Item({1}, {2})",
       EmitBase(), EmitIndex(), val);
  }

  public override string EmitLocation() {
    return String.Format("{0}get_location({1})", EmitBase(), EmitIndex());
  }
}

class This : Expression {
  public override GType Check(Context ctx) {
    if (ctx.IsStatic()) {
      Error("can't access this in a static context");
      return null;
    }
    return ctx.class_;
  }

  public override GValue Eval(Env env) {
    return env.this_;
  }

  public override string Emit() { return "this"; }
}

class Base : Expression {
  Class parent_;

  public override GType Check(Context ctx) {
    if (ctx.IsStatic()) {
      Error("can't access base in a static context");
      return null;
    }
    parent_ = ctx.class_.Parent();
    if (parent_ == null) {
      Error("can't access base: class has no parent");
      return null;
    }
    return parent_;
  }

  public override GValue Eval(Env env) {
    return env.this_;
  }

  public override string Emit() { return "this"; }

  public override string EmitArrow(GType t, Member m) {
    return m.GetClass().name_ + "::"; 
  }
}

class New : Expression {
  Expression creator_;    // either a pool or null
  TypeExpr type_expr_;
  ArrayList /* of Expression */ arguments_;

  Class class_;
  Constructor constructor_;

  public New(Expression creator, TypeExpr expr, ArrayList arguments) {
    creator_ = creator;
    type_expr_ = expr;
    arguments_ = arguments;
  }

  GType Type() {
    return creator_ == null ? (GType) new Owning(class_) : class_;
  }

  public override GType TemporaryType() { return Type(); }    

  public override GType Check(Context ctx) {
    if (creator_ != null) {
      GType c = creator_.Check(ctx);
      if (c == null)
        return null;
      if (!c.BaseType().IsSubtype(PoolClass.instance_)) {
        Error("object creator must be a pool");
        return null;
      }
    }
    GType t = type_expr_.Resolve(ctx.program_);
    if (t == null)
      return null;
    class_ = (Class) t;
    if (class_.HasAttribute(Attribute.Abstract)) {
      Error("can't instantiate abstract class");
      return null;
    }
    if (creator_ != null) {
      class_.NeedDestroy(); // every pool-allocated class needs the _Destroy1 and _Destroy2 methods
      class_.SetVirtual();  // every pool-allocated class must be virtual
    }

    constructor_ = (Constructor) Invocation.CheckInvoke(this, ctx, false, class_,
                                      class_.name_, arguments_, MemberKind.Constructor);
    if (constructor_ == null)
      return null;

    GType type = Type();
    if (type is Owning)
      ctx.AddTemporary(this);
    return type;
  }

  public override Method Calls() { return constructor_; }

  public override GValue Eval(Env env) {
    GValue obj = class_.New();
    Invocation.CallMethod(env, obj, constructor_, arguments_, false);
    return obj;
  }

  public override string Emit() {
    string args = Invocation.EmitArguments(constructor_, arguments_);

    return creator_ == null ?
      EmitAllocate(class_.name_, args, LosesOwnership()) :
      String.Format("new ({0}->Alloc(sizeof({1}))) {1}({2})", creator_.Emit(), class_.name_, args);
  }
}

class ArrayInitializer : Expression {
  public readonly ArrayList /* of Expression */ initializers_;

  public ArrayInitializer(ArrayList initializers) { initializers_ = initializers; }

  public override GType Check(Context ctx) {
    Error("only static fields may have array initializers");
    return null;
  }

  public bool CheckElements(Context ctx, GType element_type) {
    foreach (Expression e in initializers_)
      if (!e.CheckConstant() || !e.Check(ctx, element_type))
        return false;
    return true;
  }

  public override GValue Eval(Env env) { Debug.Assert(false); return null; }

  public GArray Eval(ArrayType type) {
    GArray a = new GArray(type, initializers_.Count);
    for (int i = 0; i < initializers_.Count; ++i) {
      Expression e = (Expression) initializers_[i];
      a.Set(i, e.Eval(Env.static_, type.ElementType()));
    }
    return a;
  }

  public override string Emit() { Debug.Assert(false); return null; }

  public void Emit(SourceWriter w) {
    int count = initializers_.Count;
    int per_row = 12;

    w.Write("{ ");
    if (count > per_row) {    // format onto multiple rows
      w.WriteLine("");
      w.AddIndent();
      w.Indent();
    }
    for (int i = 0; i < count; ++i) {
      if (i > 0) {
        w.Write(", ");
        if (i % per_row == 0) {
          w.WriteLine("");
          w.Indent();
        }
      }
      Expression e = (Expression) initializers_[i];
      SimpleValue v = (SimpleValue) e.Eval(Env.static_);
      w.Write(v.Emit());
    }
    if (count > per_row) {
      w.SubIndent();
      w.WriteLine("");
    }
    w.Write(" }");
  }
}

class NewArray : Expression {
  TypeExpr element_type_expr_;
  int dimensions_;
  ArrayType array_type_;

  Expression count_;

  public NewArray(TypeExpr element_type_expr, int dimensions, Expression count) {
    element_type_expr_ = element_type_expr;
    dimensions_ = dimensions;
    count_ = count;
  }

  GType Type() {
    return (GType) new Owning(array_type_);
  }

  public override GType TemporaryType() { return Type(); }

  public override GType Check(Context ctx) {
    if (element_type_expr_ is ArrayTypeExpr) {
      Error("syntax error in new expression");
      return null;
    }
    for (int i = 0; i < dimensions_; ++i)
      element_type_expr_ = new ArrayTypeExpr(element_type_expr_);
    GType element_type = element_type_expr_.Resolve(ctx.program_);
    if (element_type == null)
      return null;
    array_type_ = new ArrayType(element_type);

    if (!count_.Check(ctx, GInt.type_))
      return null;

    GType t = Type();
    if (t is Owning)
      ctx.AddTemporary(this);
    return t;
  }

  public override GValue Eval(Env env) {
    return new GArray(array_type_, count_.EvalInt(env));
  }

  public override string Emit() {
    GType t = array_type_.ElementType();
    string array_type = GType.ConstructType(
      t is Owning ? "_Array" : "_CopyableArray",
      t.EmitGenericType());
    string args = String.Format("&typeid({0}), {1}", t.EmitType(), count_.Emit());
    return EmitAllocate(array_type, args, LosesOwnership());
  }
}

abstract class Unary : Expression {
  protected Expression exp_;

  protected Unary(Expression e) { exp_ = e; }

  public override bool IsConstant() { return exp_.IsConstant(); }
}

// unary minus operator
class Minus : Unary {
  GType type_;

  public Minus(Expression e) : base(e) { }

  public override GType Check(Context ctx) {
    type_ = exp_.Check(ctx);
    if (type_ != GInt.type_ && type_ != GFloat.type_ && type_ != GDouble.type_) {
      Error("- operator cannot be applied to value of type {0}", type_);
      return null;
    }
    return type_;
  }

  public override GValue Eval(Env env) {
    if (type_ == GInt.type_) {
    int i = exp_.EvalInt(env);
    return new GInt(-i);
  }
    if (type_ == GFloat.type_) {
      float f = exp_.EvalFloat(env);
      return new GFloat(-f);
    }
    if (type_ == GDouble.type_) {
      double d = exp_.EvalDouble(env);
      return new GDouble(-d);
    }
    Debug.Assert(false);
    return null;
  }

  public override string Emit() { return "-" + exp_.Emit(); }
}

class Not : Unary {
  public Not(Expression e) : base(e) { }

  public override GType Check(Context ctx) {
    return exp_.Check(ctx, GBool.type_) ? GBool.type_ : null;
  }

  public override GValue Eval(Env env) {
    bool b = exp_.EvalBool(env);
    return new GBool(!b);
  }

  public override string Emit() { return "!" + exp_.Emit(); }
}

// the bitwise complement (~) operator
class Complement : Unary {
  public Complement(Expression e) : base(e) { }

  public override GType Check(Context ctx) {
    return exp_.Check(ctx, GInt.type_) ? GInt.type_ : null;
  }

  public override GValue Eval(Env env) {
    int i = exp_.EvalInt(env);
    return new GInt(~i);
  }

  public override string Emit() { return "~" + exp_.Emit(); }
}

class IncDec : Expression {
  bool pre_;    // true for pre-increment/decrement; false for post-increment/decrement
  bool inc_;
  LValue lvalue_;

  public IncDec(bool pre, bool inc, LValue lvalue) { pre_ = pre; inc_ = inc; lvalue_ = lvalue; }

  public override GType Check(Context ctx) {
    // We don't bother to store a node in the control graph indicating that this lvalue
    // is written after we read it; it must be valid when it's read, and writing it afterward
    // has no effect on liveness.
    GType t = lvalue_.Check(ctx, true, true, false);
    if (t == null)
      return null;
    if (lvalue_.Kind() == ExprKind.Indexer) {
      Error("++ and -- can't yet operate on indexers");
      return null;
    }
    if (t != GInt.type_) {
      Error("++ and -- can operate only on integers");
      return null;
    }
    return GInt.type_;
  }

  public override GValue Eval(Env env) {
    Location loc = lvalue_.EvalLocation(env);
    GInt i = (GInt) loc.value_;
    GInt j = new GInt(inc_ ? i.i_ + 1 : i.i_ - 1);
    loc.value_ = j;
    return pre_ ? j : i;
  }

  string EmitOp() { return inc_ ? "++" : "--"; }

  public override string Emit() {
    return pre_ ? EmitOp() + lvalue_.Emit() : lvalue_.Emit() + EmitOp();
  }
}

abstract class Conversion : Expression {
  protected Expression expr_;
  protected TypeExpr type_expr_;

  protected GType from_base_;
  protected GType to_type_, to_base_;

  protected Conversion(Expression expr, TypeExpr type_expr) {
    expr_ = expr; type_expr_ = type_expr;
  }

  protected bool CheckConversion(Context ctx, bool subtype_only) {
    GType from = expr_.Check(ctx);
    if (from == null)
      return false;
    from_base_ = from.BaseType();

    to_base_ = type_expr_.Resolve(ctx.program_);
    if (to_base_ == null)
      return false;
    if (!to_base_.IsValue() &&
       (from is Owning || from_base_.IsValue()))
      to_type_ = new Owning(to_base_);
    else to_type_ = to_base_;

    if (!from.CanConvertExplicit(to_type_, subtype_only)) {
      Error("can't convert from {0} to {1}", from_base_, to_base_);
      return false;
    }
    return true;
  }
}

class Cast : Conversion {
  public Cast(Expression expr, TypeExpr type_expr) : base(expr, type_expr) { }

  public override Local GetLocal() { return expr_.GetLocal(); }
  
  public override GType Check(Context ctx) {
    return CheckConversion(ctx, false) ? to_type_ : null;
  }

  public override int Kind() {
    return expr_.Kind() == ExprKind.Local ? ExprKind.Local : ExprKind.Value;
  }

  public override void LoseOwnership() {
    base.LoseOwnership();
    expr_.LoseOwnership();
  }

  public override GValue Eval(Env env) {
    return expr_.Eval(env).ConvertExplicit(to_base_);
  }

  public override string Emit() {
    return expr_.EmitExplicit(from_base_, to_type_);
  }
}

class Binary : Expression {
  int op_;
  Expression left_, right_;
  GType left_type_, right_type_;
  GType type_;

  const int CONCATENATE = 0;

  public Binary(Expression left, int op, Expression right) {
    left_ = left; op_ = op; right_ = right;
  }

  public static GType Promote(Syntax caller, GType left, int op, GType right) {
    if (left.CanConvert(GInt.type_) && right.CanConvert(GInt.type_))
      return GInt.type_;
    if (left.CanConvert(GFloat.type_) && right.CanConvert(GFloat.type_))
      return GFloat.type_;
    if (left.CanConvert(GDouble.type_) && right.CanConvert(GDouble.type_))
      return GDouble.type_;
    caller.Error("can't apply operator {0} to operands of type {1} and {2}", OpName(op), left, right);
    return null;
  }

  bool CheckIntArgs() {
    if (left_type_.CheckConvert(this, GInt.type_) && right_type_.CheckConvert(this, GInt.type_)) {
      type_ = GInt.type_;
      return true;
    }
    return false;
  }

  public override GType Check(Context ctx) {
    left_type_ = left_.CheckAndHold(ctx);
    right_type_ = right_.Check(ctx);
    if (left_type_ == null || right_type_ == null)
      return null;
    left_.ReleaseRef(ctx);

    if (op_ == '+' && (left_type_ == GString.type_ || right_type_ == GString.type_)) {
      op_ = CONCATENATE;

      // If we'll be calling ToString() on a class, ensure that it will inherit from
      // Object in generated code.
      if (left_type_ != GString.type_)
        left_type_.CanConvert(GObject.type_);
      if (right_type_ != GString.type_)
        right_type_.CanConvert(GObject.type_);

      return GString.type_;
    }

    switch (op_) {
      case '*':
      case '/':
      case '+':
      case '-':
        type_ = Promote(this, left_type_, op_, right_type_);
        return type_;

      case '%':
      case Parser.OP_LEFT_SHIFT:
      case Parser.OP_RIGHT_SHIFT:
        return CheckIntArgs() ? GInt.type_ : null;

      case '&':
      case '|':
        if (left_type_ == GBool.type_ && right_type_ == GBool.type_)
          type_ = GBool.type_;
        else {
          if (!CheckIntArgs())
            return null;
          type_ = GInt.type_;
        }
        return type_;

      case '<':
      case Parser.OP_LE:
      case '>':
      case Parser.OP_GE:
        type_ = Promote(this, left_type_, op_, right_type_);
        return type_ == null ? null : GBool.type_;

      default:
        Debug.Assert(false);
        return null;
    }
  }

  public static GBool BoolOp(bool x, int op, bool y) {
    switch (op) {
      case '&': return new GBool(x & y);
      case '|': return new GBool(x | y);
      default: Debug.Assert(false); return null;
    }
  }

  public static GValue IntOp(int x, int op, int y) {
    switch (op) {
      case '*': return new GInt(x * y);
      case '/': return new GInt(x / y);
      case '%': return new GInt(x % y);
      case '+': return new GInt(x + y);
      case '-': return new GInt(x - y);
      case Parser.OP_LEFT_SHIFT: return new GInt(x << y);
      case Parser.OP_RIGHT_SHIFT: return new GInt(x >> y);
      case '&': return new GInt(x & y);
      case '|': return new GInt(x | y);
      case '<': return new GBool(x < y);
      case Parser.OP_LE: return new GBool(x <= y);
      case '>': return new GBool(x > y);
      case Parser.OP_GE: return new GBool(x >= y);
      default: Debug.Assert(false); return null;
    }
  }

  public static GValue FloatOp(float x, int op, float y) {
    switch (op) {
      case '*': return new GFloat(x * y);
      case '/': return new GFloat(x / y);
      case '+': return new GFloat(x + y);
      case '-': return new GFloat(x - y);
      case '<': return new GBool(x < y);
      case Parser.OP_LE: return new GBool(x <= y);
      case '>': return new GBool(x > y);
      case Parser.OP_GE: return new GBool(x >= y);
      default: Debug.Assert(false); return null;
    }
  }

  public static GValue DoubleOp(double x, int op, double y) {
    switch (op) {
      case '*': return new GDouble(x * y);
      case '/': return new GDouble(x / y);
      case '+': return new GDouble(x + y);
      case '-': return new GDouble(x - y);
      case '<': return new GBool(x < y);
      case Parser.OP_LE: return new GBool(x <= y);
      case '>': return new GBool(x > y);
      case Parser.OP_GE: return new GBool(x >= y);
      default: Debug.Assert(false); return null;
    }
  }

  public override GValue Eval(Env env) {
    if (op_ == CONCATENATE)
      return new GString(left_.Eval(env).ToString() + right_.Eval(env).ToString());

    if (type_ == GBool.type_)
      return BoolOp(left_.EvalBool(env), op_, right_.EvalBool(env));
    if (type_ == GInt.type_)
      return IntOp(left_.EvalInt(env), op_, right_.EvalInt(env));
    if (type_ == GFloat.type_)
      return FloatOp(left_.EvalFloat(env), op_, right_.EvalFloat(env));
    if (type_ == GDouble.type_)
      return DoubleOp(left_.EvalDouble(env), op_, right_.EvalDouble(env));

    Debug.Assert(false);
    return null;
  }

  public override bool IsConstant() { return left_.IsConstant() && right_.IsConstant(); }

  static string OpName(int op) {
    switch (op) {
      case Parser.OP_LEFT_SHIFT: return "<<";
      case Parser.OP_RIGHT_SHIFT: return ">>";
      case Parser.OP_LE: return "<=";
      case Parser.OP_GE: return ">=";
      default: return ((char) op).ToString();
    }
  }

  public override string Emit() {
    if (op_ == CONCATENATE)
      return Hold(GString.type_,
        String.Format("String::_Concat({0}, {1})",
          left_.EmitRef(left_type_, GObject.type_), right_.Emit(right_type_, GObject.type_)));

    return String.Format("{0} {1} {2}",
      left_.EmitRef(left_type_), OpName(op_), right_.Emit() );
  }
}

class Equality : Expression {
  bool equal_;    // true for ==, false for !=
  Expression left_, right_;
  GType left_type_, right_type_, type_;

  public Equality(Expression left, int op, Expression right) {
    left_ = left;
    switch (op) {
      case Parser.OP_EQUAL: equal_ = true; break;
      case Parser.OP_NE: equal_ = false; break;
      default: Debug.Assert(false); break;
    }
    right_ = right;
  }

  public override GType Check(Context ctx) {
    left_type_ = left_.CheckAndHold(ctx);
    right_type_ = right_.Check(ctx);
    if (left_type_ == null || right_type_ == null)
      return null;
    left_.ReleaseRef(ctx);
    if (left_type_.IsReference() != right_type_.IsReference()) {
      Error("can't compare types {0} and {1}", left_type_, right_type_);
      return null;
    }
    type_ = left_type_.BaseType().CommonType(this, right_type_.BaseType());
    return type_ == null ? null : GBool.type_;
  }

  public override GValue Eval(Env env) {
    GValue left = left_.Eval(env, type_);
    GValue right = right_.Eval(env, type_);
    bool eq = left.DefaultEquals(right);
    return new GBool(equal_ ? eq : !eq);
  }

  public override bool IsConstant() { return left_.IsConstant() && right_.IsConstant(); }

  public override string Emit() {
    string emit_left = left_.EmitRef(left_type_);
    string emit_right = right_.Emit();
    if (type_ == GString.type_)
      return String.Format("{0}String::_Equals({1}, {2})",
                           equal_ ? "" : "!", emit_left, emit_right);
    return String.Format("{0} {1} {2}", emit_left, equal_ ? "==" : "!=", emit_right);
  }
}

class Is : Conversion {
  public Is(Expression expr, TypeExpr type_expr) : base(expr, type_expr) { }

  public override GType Check(Context ctx) {
    return CheckConversion(ctx, true) ? GBool.type_ : null;
  }

  public override GValue Eval(Env env) {
    GValue v = expr_.Eval(env);
    return new GBool(!(v is Null) && v.Type().IsSubtype(to_base_));
  }

  public override string Emit() {
    if (from_base_.IsReference()) {
      Class c = (Class) to_base_;
      return String.Format("dynamic_cast<{0} *>({1}) != 0", c.name_, expr_.Emit());
    }
    return to_base_ == from_base_ || to_base_ == GObject.type_ ? "true" : "false";
  }
}

class As : Conversion {
  public As(Expression expr, TypeExpr type_expr) : base(expr, type_expr) { }

  public override Local GetLocal() { return expr_.GetLocal(); }

  public override GType Check(Context ctx) {
    if (!CheckConversion(ctx, true))
      return null;
    if (!to_base_.IsReference()) {
      Error("as operator must convert to reference type");
      return null;
    }
    return to_type_;
  }

  public override int Kind() {
    return expr_.Kind() == ExprKind.Local ? ExprKind.Local : ExprKind.Value;
  }

  public override void LoseOwnership() {
    base.LoseOwnership();
    expr_.LoseOwnership();
  }

  public override GValue Eval(Env env) {
    GValue v = expr_.Eval(env);
    return v.Type().IsSubtype(to_base_) ? v : Null.Instance;
  }

  public override string Emit() {
    if (from_base_.CanConvert(to_base_))
      return expr_.Emit(from_base_, to_base_);
    Class c = (Class) to_base_;
    return String.Format("dynamic_cast<{0} *>({1})", c.name_, expr_.Emit());
  }
}

// a && or || operator
class LogicalOp : Expression {
  bool and_;  // true => &&, false => ||
  Expression left_, right_;
  Joiner join_ = new Joiner();

  public LogicalOp(Expression left, int op, Expression right) {
    left_ = left;
    switch (op) {
      case Parser.OP_AND: and_ = true; break;
      case Parser.OP_OR: and_ = false; break;
      default: Debug.Assert(false); break;
    }
    right_ = right;
  }

  public override GType Check(Context ctx) {
    if (!left_.Check(ctx, GBool.type_))
      return null;

    join_.Join(ctx.Prev());
    if (!right_.Check(ctx, GBool.type_))
      return null;

    join_.Join(ctx.Prev());
    ctx.SetPrev(join_.Combine());

    return GBool.type_;
  }

  public override GValue Eval(Env env) {
    bool left = left_.EvalBool(env);
    bool b = and_ ? left && right_.EvalBool(env) : left || right_.EvalBool(env);
    return new GBool(b);
  }

  public override bool IsConstant() { return left_.IsConstant() && right_.IsConstant(); }

  public override string Emit() {
    return String.Format("{0} {1} {2}", left_.Emit(), and_ ? "&&" : "||", right_.Emit());
  }

}

// the ?: operator
class Conditional : Expression {
  Expression condition_;
  Expression if_true_, if_false_;

  GType true_type_, false_type_;
  GType type_;
  Joiner join_ = new Joiner();

  public Conditional(Expression condition, Expression if_true, Expression if_false) {
    condition_ = condition; if_true_ = if_true; if_false_ = if_false;
  }

  public override GType Check(Context ctx) {
    if (!condition_.Check(ctx, GBool.type_))
      return null;

    Control c = ctx.Prev();

    true_type_ = if_true_.Check(ctx);
    if (true_type_ == null)
      return null;

    join_.Join(ctx.Prev());
    ctx.SetPrev(c);

    false_type_ = if_false_.Check(ctx);
    if (false_type_ == null)
      return null;

    join_.Join(ctx.Prev());
    ctx.SetPrev(join_.Combine());

    type_ = true_type_.CommonType(this, false_type_);
    return type_;
  }

  public override int Kind() {
    return if_true_.Kind() == ExprKind.Local && if_false_.Kind() == ExprKind.Local ? ExprKind.Local : ExprKind.Value;
  }

  public override void LoseOwnership() {
    base.LoseOwnership();
    if_true_.LoseOwnership();
    if_false_.LoseOwnership();
  }

  public override GValue Eval(Env env) {
    return condition_.EvalBool(env) ? if_true_.Eval(env, type_) : if_false_.Eval(env, type_);
  }

  public override string Emit() {
    return String.Format("{0} ? {1} : {2}", condition_.Emit(),
                         if_true_.Emit(true_type_, type_), if_false_.Emit(false_type_, type_));
  }
}

class Assign : Expression {
  LValue left_;
  Expression right_;

  GType left_type_, right_type_;

  public Assign(LValue left, Expression right) {
    left_ = left; right_ = right;
  }

  public static bool CheckAssign(Syntax caller, GType left_type, Expression right, GType right_type) {
    if (!right_type.CheckConvert(caller, left_type,
      right.Kind() == ExprKind.Local ? ConversionContext.AssignVar : ConversionContext.Other))
      return false;
    right.CheckLoseOwnership(right_type, left_type);
    return true;
  }

  public override GType Check(Context ctx) {
    left_type_ = left_.Check(ctx, false, true, false);
    if (left_type_ == null)
      return null;

    right_type_ = right_.Check(ctx, true, false, false);
    if (right_type_ == null || !CheckAssign(this, left_.StorageType(), right_, right_type_))
      return null;
    right_.HoldRef(ctx);

    AddControl(ctx);
    left_.ReleaseAll(ctx);
    right_.ReleaseRef(ctx);
    return left_type_;
  }

  public override Method Calls() {
    PropertyOrIndexer pi = left_.GetPropertyOrIndexer();
    return pi == null ? null : pi.Setter();
  }

  public override bool Sets(Local local) { return left_.IsLocal(local); }

  public override TypeSet NodeDestroys() {
    return left_.GetPropertyOrIndexer() != null ? TypeSet.empty_ : left_.StorageType().VarDestroys();
  }

  public override GValue Eval(Env env) {
    GValue v1, v2;
    left_.Eval1(env, out v1, out v2);
    GValue val = right_.Eval(env, left_type_);
    left_.EvalSet(env, v1, v2, val);
    return val;
  }

  public override string Emit() {
    return left_.EmitSet(right_.EmitRef(right_type_, left_type_));
  }
}

// a compound assignment operator such as += or -=
class CompoundAssign : Expression {
  LValue left_;
  int op_;
  Expression right_;

  GType type_;

  public CompoundAssign(LValue left, int op, Expression right) {
    left_ = left; op_ = op; right_ = right;
  }

  public override GType Check(Context ctx) {
    // We don't bother to store a node in the control graph indicating that this lvalue
    // is written after we read it; it must be valid when it's read, and writing it afterward
    // has no effect on liveness.
    type_ = left_.Check(ctx, true, true, false);
    if (type_ == null)
      return null;
    if (left_.Kind() == ExprKind.Indexer) {
      Error("compound assignments don't yet work on indexers");
      return null;
    }
    if (type_ == GBool.type_ && (op_ == '&' || op_ == '|')) {
      if (!right_.Check(ctx, GBool.type_))
        return null;
      return type_;
    }
    if (type_ != GInt.type_ && type_ != GFloat.type_ && type_ != GDouble.type_) {
      Error("compound assignment operator {0}= can't operate on object of type {1}", (char) op_, type_);
      return null;
    }
    if (!right_.Check(ctx, type_))
      return null;
    return type_;
  }

  public override GValue Eval(Env env) {
    Location loc = left_.EvalLocation(env);
    if (type_ == GBool.type_) {
      GBool x = (GBool) loc.value_;
      bool y = right_.EvalBool(env);
      GBool z = Binary.BoolOp(x.b_, op_, y);
      loc.value_ = z;
      return z;
    } else if (type_ == GInt.type_) {
      GInt x = (GInt) loc.value_;
      int y = right_.EvalInt(env);
      GInt z = (GInt) Binary.IntOp(x.i_, op_, y);
      loc.value_ = z;
      return z;
    } else if (type_ == GFloat.type_) {
      GFloat x = (GFloat) loc.value_;
      float y = right_.EvalFloat(env);
      GFloat z = (GFloat) Binary.FloatOp(x.f_, op_, y);
      loc.value_ = z;
      return z;
    } else if (type_ == GDouble.type_) {
      GDouble x = (GDouble) loc.value_;
      double y = right_.EvalDouble(env);
      GDouble z = (GDouble) Binary.DoubleOp(x.d_, op_, y);
      loc.value_ = z;
      return z;
    } else {
      Debug.Assert(false);
      return null;
    }
  }

  public override string Emit() {
    return String.Format("{0} {1}= {2}", left_.Emit(), (char) op_, right_.Emit());
  }
}

class Take : Expression {
  LValue exp_;
  Owning type_;

  public Take(LValue exp) { exp_ = exp; }

  public override GType Check(Context ctx) {
    GType t = exp_.Check(ctx);
    if (t == null)
      return null;
    type_ = exp_.StorageType() as Owning;
    if (type_ == null) {
      Error("take must be applied to owning pointer");
      return null;
    }
    exp_.LoseOwnership();
    ctx.AddTemporary(this);
    return type_;
  }

  public override GType TemporaryType() { return type_; }

  public override GValue Eval(Env env) {
    GValue v = exp_.Eval(env);
    exp_.EvalSet(env, Null.Instance);
    return v;
  }

  public override string Emit() {
    return Hold(exp_.StorageType(), exp_.Emit());
  }
}

abstract class Statement : Node {
  public abstract bool Check(Context ctx);
  public abstract GValue Eval(Env env);

  public abstract void Emit(SourceWriter w);

  public virtual void EmitEmbedded(SourceWriter w) {
    w.WriteLine("");
    w.AddIndent();
    w.Indent();
    Emit(w);
    w.SubIndent();
  }

  public virtual void EmitInExistingBlock(SourceWriter w) {
    w.Indent();
    Emit(w);
  }
}

class StatementList {
  public readonly ArrayList /* of Statement */ statements_ = new ArrayList();

  public void Add(Statement s) { statements_.Add(s); }

  public bool Check(Context ctx) {
    bool ok = true;
    foreach (Statement s in statements_)
      ok &= s.Check(ctx);
    return ok;
  }

  public GValue Eval(Env env) {
    foreach (Statement s in statements_) {
      GValue v = s.Eval(env);
      if (v != null)
        return v;
    }
    return null;
  }

  public void Emit(SourceWriter w) {
    foreach (Statement s in statements_) {
      w.Indent();
      s.Emit(w);
    }
  }
}

class EmptyStatement : InlineStatement {
  public EmptyStatement() { }

  public override bool Check(Context ctx) { return true; }
  public override GValue Eval(Env env) { return null; }
  public override void EmitInline(SourceWriter w) { }

  public static readonly EmptyStatement instance_ = new EmptyStatement();
}

// A Scoped is a statement defining one or more local variables.  If a Scoped appears in the
// control graph, then it destroys those variables when traversed.
abstract class Scoped : Statement {
  protected Local start_;   // the first local outside this statement
  protected Local top_;     // the top local defined inside this statement

  protected void SetStartVar(Context ctx) { start_ = top_ = ctx.var_; }
  protected void SetTopVar(Context ctx) { top_ = ctx.var_; }

  public Local GetStart() { return start_; }
  public Local GetTop() { return top_; }

  public override TypeSet NodeDestroys() {
    TypeSet set = new TypeSet();
    for (Local l = top_; l != start_; l = l.next_)
      set.Add(l.Type().VarDestroys());
    return set;
  }
}

class Block : Scoped {
  public readonly StatementList list_;

  public Block(StatementList list) { list_ = list; }

  public bool Absent() { return list_ == null; }

  public override bool Check(Context ctx) {
    Context ctx1 = new Context(ctx);   // don't pass declarations outside block
    SetStartVar(ctx1);

    if (!list_.Check(ctx1))
      return false;

    SetTopVar(ctx1);

    // Add this Block to the control graph; this represents the destruction of variables
    // within the block at block exit.
    AddControl(ctx1);

    return true;
  }

  public override GValue Eval(Env env) {
    return list_.Eval(new Env(env));
  }

  public static Block EmptyBlock() { return new Block(new StatementList()); }

  public override void Emit(SourceWriter w) {
    w.OpenBrace();
    list_.Emit(w);
    w.CloseBrace();
  }

  public override void EmitEmbedded(SourceWriter w) {
    w.Write(" ");
    Emit(w);
  }

  public override void EmitInExistingBlock(SourceWriter w) {
    list_.Emit(w);
  }
}

class MemberKind {
  public const int
    Field = 0,
    Method = 1,
    Property = 2,
    Indexer = 3,
    Constructor = 4;
}

class Named : Node {
  public readonly TypeExpr type_expr_;   // may be null for certain objects such as constructors
  protected GType type_;

  public readonly string name_;

  public Named(TypeExpr type_expr, string name) {
    type_expr_ = type_expr;
    name_ = name;
  }

  public GType Type() { return type_; }

  public virtual bool Resolve(Program program) {
    if (type_expr_ == null || type_ != null)
      return true;
    type_ = type_expr_.Resolve(program);
    return type_ != null;
  }
}

abstract class Member : Named {
  protected Class class_;    // containing class

  public readonly int attributes_;

  protected Member(int attributes, TypeExpr type_expr, string name) : base(type_expr, name) {
    attributes_ = attributes;
  }

  public abstract int Kind();

  public static string ToString(int kind) {
    switch (kind) {
      case MemberKind.Field: return "field";
      case MemberKind.Method: return "method";
      case MemberKind.Property: return "property";
      case MemberKind.Indexer: return "indexer";
      case MemberKind.Constructor: return "constructor";
      default: Debug.Assert(false); return null;
    }
  }

  public string KindString() { return ToString(Kind()); }

  protected abstract int ValidAttributes();

  public Class GetClass() { return class_; }
  public void SetClass(Class cl) { class_ = cl; }

  public bool HasAttribute(int a) {
    return ((attributes_ & a) != 0);
  }

  public bool IsOverride() { return HasAttribute(Attribute.Override); }

  public bool IsProtected() { return HasAttribute(Attribute.Protected); }

  public bool IsPublic() { return HasAttribute(Attribute.Public); }

  public bool IsPrivate() { return !HasAttribute(Attribute.Public | Attribute.Protected); }

  public bool IsVirtual() {
    return HasAttribute(Attribute.Virtual | Attribute.Abstract | Attribute.Override);
  }

  static ArrayList empty_ = new ArrayList();

  public virtual ArrayList /* of Parameter */ Parameters() { return empty_; }

  public Parameter Param(int i) {
    return (Parameter) Parameters()[i];
  }

  public bool IsAccessible(Class from_class, GType through_type, bool through_base) {
    if (IsPublic())
      return true;

    // In GEL2 (as in C# and C++), a class [from_class] can access a protected member of
    // a class C only if [from_class] is a subtype of C and the access is
    // through a subtype of [from_class].  See e.g. C# 3.5.3 "Protected Access for Instance Members".
    if (IsProtected())
      return through_base || from_class.IsSubtype(class_) && through_type.IsSubtype(from_class);

    // default private access
    return from_class == class_;
  }

  public static bool MatchKind(int kind1, int kind2) {
    return kind1 == kind2 ||
           kind1 == MemberKind.Field && kind2 == MemberKind.Property ||
           kind1 == MemberKind.Property && kind2 == MemberKind.Field;
  }

  public bool MatchSignature(Member m) {
    if (!MatchKind(Kind(), m.Kind()))
      return false;

    if (name_ != m.name_ || Parameters().Count != m.Parameters().Count)
      return false;

    int i = 0;
    foreach (Parameter p in m.Parameters()) {
      Parameter q = Param(i);
      if (!p.Match(q))
        return false;
      ++i;
    }
    return true;
  }

  public bool IsApplicable(ArrayList arguments, out int mismatches) {
    mismatches = 0;
    if (Parameters().Count != arguments.Count)
      return false;
    int i = 0;
    foreach (Argument a in arguments) {
      if (!Param(i).CanReceive(a))
        ++mismatches;
      ++i;
    }
    return true;
  }

  public void ReportMismatches(Syntax caller, ArrayList arguments) {
    Debug.Assert(Parameters().Count == arguments.Count);
    int i = 0;
    foreach (Argument a in arguments) {
      Parameter p = Param(i);
      if (!p.CanReceive(a))
        caller.Error("argument {0}: can't convert from {1} to {2}", i + 1, a.TypeString(), p.TypeString());
      ++i;
    }
  }

  protected virtual void AddOverride(Member m) { }

  bool CheckOverride(Context ctx) {
    Class parent = ctx.class_.Parent();
    Member m = parent != null ? parent.FindMatchingMember(this, false) : null;
    if (m != null) {
      if (this is Field) {
        Error("can't define field with same name as field/property in superclass");
        return false;
      }
      if (m is Field) {
        Error("can't define property with same name as field in superclass");
        return false;
      }
      if (!HasAttribute(Attribute.Override)) {
        Error("must use override keyword to override superclass {0}", KindString());
        return false;
      }
      if (HasAttribute(Attribute.Virtual)) {
        Error("{0} with override or abstract keyword cannot be marked virtual", KindString());
        return false;
      }
      if (!m.IsVirtual()) {
        Error("attempting to override non-virtual {0}", KindString());
        return false;
      }
      if (!m.Type().Equals(Type())) {
        Error("can't override {0} with different return type", KindString());
        return false;
      }
      if (m.IsPublic() != IsPublic()) {
        Error("override {0} must have same accessibility as {0} it overrides", KindString());
        return false;
      }
      m.AddOverride(this);
    } else if (HasAttribute(Attribute.Override)) {
      Error("can't find {0} to override", KindString());
      return false;
    }
    return true;
  }

  bool CheckAccessibility() {
    int n = 0;
    if ((attributes_ & Attribute.Private) != 0)
      ++n;
    if ((attributes_ & Attribute.Protected) != 0)
      ++n;
    if ((attributes_ & Attribute.Public) != 0)
      ++n;
    if (n > 1) {
      Error("only one access level (private, protected, public) may be specified");
      return false;
    }

    if (IsVirtual() && IsPrivate()) {
      Error("a virtual or abstract {0} may not be private", KindString());
      return false;
    }

    return true;
  }

  public virtual bool Check(Context ctx) {
    if (!AttributeUtil.CheckOnly(attributes_, ValidAttributes())) {
      Error("illegal {0} attribute", KindString());
      return false;
    }

    if (HasAttribute(Attribute.Abstract) && !ctx.class_.HasAttribute(Attribute.Abstract)) {
      Error("an abstract {0} can be defined only in an abstract class", KindString());
      return false;
    }

    if (name_ == class_.name_ && !(this is Constructor)) {
      Error("{0} cannot have same name as enclosing class", KindString());
      return false;
    }

    if (class_.GetMatchingMember(this) != this) {
      Error("{0} {1} appears twice in same class", KindString(), name_ != null ? name_ : "");
      return false;
    }

    return CheckOverride(ctx) && CheckAccessibility();
  }
}

// a member which represents a storage location: a field, property, or indexer
abstract class LMember : Member {
  protected LMember(int attributes, TypeExpr type_expr, string name)
    : base(attributes, type_expr, name) { }

  public bool IsConstOrStatic() {
    return HasAttribute(Attribute.Const | Attribute.Static);
  }

  protected bool CheckStatic(Syntax caller, bool static_ok, bool instance_ok) {
    if (!static_ok && IsConstOrStatic()) {
      caller.Error("{0} {1} is static", KindString(), name_);
      return false;
    }
    if (!instance_ok && !IsConstOrStatic()) {
      caller.Error("{0} {1} is not static", KindString(), name_);
      return false;
    }
    return true;
  }

  public abstract bool CheckAssigning(Syntax caller, Context ctx, bool assigning);

  public bool CheckAccess(Syntax caller, Context ctx, bool assigning, bool static_ok, bool instance_ok) {
    return CheckStatic(caller, static_ok, instance_ok) && CheckAssigning(caller, ctx, assigning);
  }

  public abstract Location GetLocation(GObject obj);

  // These methods are for fields and properties; indexers have their own versions which take
  // an extra index argument.
  public virtual GValue Get(GValue obj) { Debug.Assert(false); return null; }
  public virtual void Set(GObject obj, GValue val)  { Debug.Assert(false); }

  public virtual string Emit()  { Debug.Assert(false); return null; }
  public virtual string EmitSet(string val)  { Debug.Assert(false); return null; }
}

class Field : LMember {
  protected Expression initializer_;    // or null if none
  protected GType initializer_type_;

  public Field(int attributes, TypeExpr type_expr, string name, Expression initializer)
  : base(attributes, type_expr, name) {
    initializer_ = initializer;
  }

  // Create a field for an internal class.  Such fields will never be type checked.
  public Field(GType type, string name) : this(Attribute.Public | Attribute.ReadOnly, null, name, null) {
    type_ = type;
  }

  public static Field New(int attributes, TypeExpr type_expr, string name, Expression initializer) {
    if ((attributes & Attribute.Static) != 0)
      return new StaticField(attributes, type_expr, name, initializer);
    if ((attributes & Attribute.Const) != 0)
      return new ConstField(attributes, type_expr, name, initializer);
    return new Field(attributes, type_expr, name, initializer);
  }

  public override int Kind() { return MemberKind.Field; }

  public Expression Initializer() { return initializer_; }

  protected virtual bool CheckInitializer(Context ctx) {
    if (initializer_ == null)
      return true;

    ctx.EnterExpression();
    initializer_type_ = initializer_.Check(ctx);
    bool b = initializer_type_ != null &&
             Assign.CheckAssign(this, type_, initializer_, initializer_type_);
    ctx.FinishExpression();
    return b;
  }

  protected override int ValidAttributes() {
    return Attribute.Const | Attribute.Private | Attribute.Protected | Attribute.Public |
           Attribute.ReadOnly | Attribute.Static;
  }

  public override bool Check(Context ctx) {
    if (!base.Check(ctx))
      return false;

    // We build up a small control graph even when checking field initializers; we do this
    // since expression-checking code uses the graph to determine when temporary
    // ref counts are needed.
    prev_ = unreachable_;
    ctx.SetPrev(this);

    bool b = CheckInitializer(ctx);

    ctx.ClearPrev();
    return b;
  }

  public override bool CheckAssigning(Syntax caller, Context ctx, bool assigning) {
    if (assigning) {
      if (HasAttribute(Attribute.Const)) {
        caller.Error("can't assign to const field");
        return false;
      }
      if (HasAttribute(Attribute.ReadOnly) &&
          (ctx.class_ != class_ || !(ctx.method_ is Constructor)) ) {
        caller.Error("readonly field {0} can only be assigned in constructor", name_);
        return false;
      }
    }
    return true;
  }

  public override GValue Get(GValue obj) { return obj.Get(this); }
  public override void Set(GObject obj, GValue val) { obj.Set(this, val); }
  public override Location GetLocation(GObject obj) { return obj.GetLocation(this); }

  protected void WriteField(SourceWriter w, bool declaration) {
    w.Indent();
    if (declaration && IsConstOrStatic())
      w.Write("static ");
    w.Write("{0} ", type_.EmitType());
    if (this is ConstField)
      w.Write("const ");
    if (!declaration)
      w.Write("{0}::", class_.name_);
    w.Write(name_);
  }

  protected void WriteDeclaration(SourceWriter w) { WriteField(w, true); }
  protected void WriteDefinition(SourceWriter w) { WriteField(w, false); }

  public virtual void EmitDeclaration(SourceWriter w) {
    WriteDeclaration(w);
    w.WriteLine(";");
  }

  public void EmitInitializer(SourceWriter w) {
    w.IWrite("{0} = ", name_);
    if (initializer_ != null)
      w.Write(initializer_.Emit(initializer_type_, type_));
    else w.Write(type_.DefaultValue().Emit());
    w.WriteLine(";");
  }

  public virtual void Emit(SourceWriter w) { }

  public override string Emit() { return name_; }

  public override string EmitSet(string val) {
    return String.Format("{0} = {1}", name_, val);
  }
}

class StaticField : Field {
  protected Location loc_;

  public StaticField(int attributes, TypeExpr type_expr, string name, Expression initializer)
    : base(attributes, type_expr, name, initializer) { }

  public override bool Check(Context ctx) {
    if (!base.Check(ctx))
      return false;
    loc_ = new Location(Type().DefaultValue());
    return true;
  }

  protected override bool CheckInitializer(Context ctx) {
    ArrayInitializer ai = initializer_ as ArrayInitializer;
    if (ai != null) {
      GType type = type_;
      Owning o = type as Owning;
      type = (o != null) ? o.BaseType() : null;
      ArrayType at = type as ArrayType;
      if (at == null) {
        Error("only owning arrays may have initializers");
        return false;
      }
      return ai.CheckElements(ctx, at.ElementType());
    }

    return base.CheckInitializer(ctx);
  }

  public virtual void Init() {
    ArrayInitializer ai = initializer_ as ArrayInitializer;
    if (ai != null)
      loc_.value_ = ai.Eval((ArrayType) type_.BaseType());
    else if (initializer_ != null)
      loc_.value_ = initializer_.Eval(Env.static_, type_);
  }

  public override GValue Get(GValue obj) { return loc_.value_; }
  public override void Set(GObject obj, GValue val) { loc_.value_ = val; }
  public override Location GetLocation(GObject obj) { return loc_; }

  public override void Emit(SourceWriter w) {
    ArrayInitializer ai = initializer_ as ArrayInitializer;
    if (ai != null) {
      GType element_type = ((ArrayType) type_.BaseType()).ElementType();
      string varname = String.Format("_{0}_{1}", class_.name_, name_);
      w.Write("{0} {1}[] = ", element_type.EmitGenericType(), varname);
      ai.Emit(w);
      w.WriteLine(";");
      WriteDefinition(w);
      w.Write("(new {0}", GType.ConstructType("_StaticArray", element_type.EmitGenericType()));
      w.Write("(&typeid({0}), {1}, {2}))", element_type.EmitType(), ai.initializers_.Count, varname);
    } else {
      WriteDefinition(w);
      if (initializer_ != null)
        initializer_.EmitAsInitializer(w, initializer_type_, type_);
    }
    w.WriteLine(";");
    w.WriteLine("");
  }
}

class ConstField : Field {
  protected SimpleValue value_;

  public ConstField(int attributes, TypeExpr type_expr, string name, Expression initializer)
    : base(attributes, type_expr, name, initializer) { }

  public override bool Check(Context ctx) {
    if (!base.Check(ctx))
      return false;
    return initializer_.CheckConstant();
  }

  SimpleValue Get() {
    if (value_ == DefaultValue.instance_) {
      Error("circular dependency among constant fields");
      Gel.Exit();
    }
    if (value_ == null) {
      value_ = new DefaultValue();    // marker used to catch circular const references
      value_ = (SimpleValue)initializer_.Eval(Env.static_, type_);
    }
    return value_;
  }

  public override GValue Get(GValue obj) {
    return Get();
  }

  public override void Set(GObject obj, GValue val) { Debug.Assert(false); }
  public override Location GetLocation(GObject obj) { Debug.Assert(false); return null; }

  public override void EmitDeclaration(SourceWriter w) {
    WriteDeclaration(w);
    if (type_ is IntegralType)
      w.Write(" = {0}", Get().Emit());
    w.WriteLine(";");
  }

  public override void Emit(SourceWriter w) {
    WriteDefinition(w);
    if (!(type_ is IntegralType)) {
      w.WriteLine(" = {0};", Get().Emit());
    }
    w.WriteLine(";");
  }
}

class ExpressionTraverser : Traverser {
  readonly Control start_;
  Local local_;
  GType type_;
  bool assign_;
  bool destroy_;

  public ExpressionTraverser(Control start, Local local, GType type) {
    start_ = start; local_ = local; type_ = type;
  }

  public override int Handle(Control control) {
    if (control == start_)
      return Cut;
    Node node = control as Node;
    if (node != null) {
      if (!destroy_ && node.CanDestroy(type_))
        destroy_ = true;
      else if (local_ != null && node.Sets(local_))
        assign_ = true;
    }
    return Continue;
  }

  // Return true if the given expression needs a reference count while we evaluate
  // the control graph nodes between [start] and [end].
  // A local variable needs a ref count if it is assigned to and the object
  // the variable previously pointed to might possibly be destroyed.
  // Any other expression needs a ref count if the object it evaluates to
  // might possibly be destroyed.
  public static bool NeedRef(Control start, Control end, Expression expr, GType type) {
    Debug.Assert(start != null && end != null);
    if (start == end || !type.IsOwned() || expr is This || expr is Base)
      return false;
    Local local = expr.GetLocal();
    ExpressionTraverser et = new ExpressionTraverser(start, local, type);
    end.Traverse(et, Control.GetMarkerValue());
    return et.destroy_ && (local == null || et.assign_);
  }
}

abstract class LocalHandler {
  public abstract bool Handle(Local local, Node node, Name use);
}

class LocalChecker : LocalHandler {
  public override bool Handle(Local local, Node node, Name use) {
    if (node == Control.unreachable_) {
      if (use != null)
        use.Error("variable {0} may be used before it is assigned a value", use.name_);
      else
        local.Error("out parameter {0} must be assigned before leaving method", local.name_);
      return false;
    }
    if (node.Takes(local)) {
      Name name = (Name) node;
      name.Error("can't transfer ownership from {0}: value may be used again", name.name_);
      return false;
    }
    return true;
  }
}

class LocalRefAnalyzer : LocalHandler {
  public override bool Handle(Local local, Node node, Name use) {
    Debug.Assert(node != Control.unreachable_);
    if (node.CanDestroy(local.Type())) {
      local.NeedsRef();
      return false;   // abort search
    }
    return true;
  }
}

class LocalTraverser : Traverser {
  readonly Local local_;
  readonly LocalHandler handler_;
  Name use_;

  public LocalTraverser(Local local, LocalHandler handler) {
    local_ = local;
    handler_ = handler;
  }

  public void SetUse(Name use) { use_ = use; }

  public override int Handle(Control control) {
    Node node = control as Node;
    if (node == null)
      return Continue;

    if (node.Sets(local_))
      return Cut;

    return handler_.Handle(local_, node, use_) ? Continue : Abort;
  }
}

class Local : Named {
  protected Expression initializer_;    // or null if none
  protected GType initializer_type_;

  public Local next_;    // next variable upward in scope chain

  ArrayList /* of Name */ uses_ = new ArrayList();    // all uses of this variable

  protected bool mutable_;   // true if this local may ever change after it's first initialized

  protected bool needs_ref_;    // true if this variable needs a reference count in emitted code

  public Expression Initializer() { return initializer_; }

  public void NeedsRef() { needs_ref_ = true; }

  public virtual string Emit() { return name_; }

  // Return true if we represent this local using a smart pointer wrapper (i.e.
  // _Ptr, _Own, _OwnRef or _Ref).
  public virtual bool IsWrapper() {
    return type_ is Owning || type_ == GString.type_ ||
      (Gel.always_ref_ ? type_.IsOwned() : needs_ref_);
  }

  public Local(TypeExpr type_expr, string name, Expression initializer) : base(type_expr, name) {
    initializer_ = initializer;
    next_ = null;
  }

  public bool Check(Context ctx) {
    Local decl = ctx.FindVar(name_);
    if (decl != null) {
      Error("error: variable already defined");
      return false;
    }

    if (initializer_ != null) {
      ctx.EnterExpression();
      initializer_type_ = initializer_.Check(ctx);
      bool ok = initializer_type_ != null &&
                Assign.CheckAssign(this, type_, initializer_, initializer_type_);

      // Add this Local to the control graph.  We need to do this before calling
      // FinishExpression since the Local will be initialized before temporaries 
      // are destroyed.
      AddControl(ctx);

      ctx.FinishExpression();
      if (!ok)
        return false;
    }

    next_ = ctx.var_;
    ctx.SetVar(this);

    ctx.method_.AddVar(this);
    return true;
  }

  public override bool Sets(Local local) {
    return this == local && initializer_ != null;
  }

  public virtual GType ReadType() {
    return type_;
  }

  public void AddUse(Name name) {
    uses_.Add(name);
  }

  public void SetMutable() { mutable_ = true; }

  // Traverse the control graph nodes where this local is live, calling the given
  // LocalHandler's Handle method on each node.
  public bool Traverse(Method method, LocalHandler h) {
    LocalTraverser t = new LocalTraverser(this, h);
    int marker = Control.GetMarkerValue();
    foreach (Name name in uses_) {
      t.SetUse(name);
      if (!name.prev_.Traverse(t, marker))
        return false;
    }

    Parameter p = this as Parameter;
    if (p != null && p.GetMode() == Mode.Out) {
      t.SetUse(null);
      if (!method.exit_.Traverse(t, marker))
        return false;
    }
    return true;
  }

  // check variable usage once control flow graph is complete
  public bool CheckUsage(Method method) {
    return Traverse(method, new LocalChecker());
  }

  // Determine whether this Local needs a reference count.  This can happen only after
  // we've built up a control graph for all methods.
  public void ComputeRef(Method method) {
    // For now, a variable of type object always needs a reference count since it may
    // contain a string and we don't yet detect string destruction in the graph traversal.
    if (type_ == GObject.type_)
      NeedsRef();
    else if (type_.IsOwned())
      Traverse(method, new LocalRefAnalyzer());
  }

  public void EvalInit(Env env) {
    env.Add(this, initializer_ != null ? initializer_.Eval(env, type_) : null);
  }

  protected virtual string EmitDeclarationType() {
    return IsWrapper() ? type_.EmitType() : type_.EmitExprType();
  }

  public virtual string EmitDeclarationName() { return name_; }

  public void EmitDeclaration(SourceWriter w) {
    w.Write("{0} {1}", EmitDeclarationType(), EmitDeclarationName());
  }

  public void EmitInitializer(SourceWriter w) {
    if (initializer_ != null)
      initializer_.EmitAsInitializer(w, initializer_type_, type_);
  }
}

class Parameter : Local {
  // We represent some parameters using both a C++ parameter and a C++ local, which
  // have different types; we call these dual parameters.  For such parameters
  // param_name_ is the C++ parameter name.
  string param_name_;

  public Parameter(TypeExpr type_expr, string name) : base(type_expr, name, null) { }

  public static Parameter New(int mode, TypeExpr type_expr, string name) {
    return mode == 0 ? new Parameter(type_expr, name) :
                               new RefOutParameter(mode, type_expr, name);
  }

  public virtual int GetMode() { return 0; }
  public string TypeString() { return Mode.ToString(GetMode()) + type_.ToString(); }

  public virtual bool CanReceive(Argument a) {
    return a.GetMode() == 0 && a.Type().CanConvert(type_, ConversionContext.MethodArg);
  }

  public bool Match(Parameter p) {
    return GetMode() == p.GetMode() && type_.Equals(p.type_);
  }

  public override bool IsWrapper() {
    return type_ is Owning || type_ == GString.type_ || mutable_ && needs_ref_;
  }

  public void CheckParameterUsage(Method method) {
    if (type_ is Owning && !(this is RefOutParameter)) {
      // We need both a C++ parameter and a C++ local: the parameter type can't be
      // _Own<>, since _Own has no public copy constructor and GCC doesn't allow passing
      // by value when a class's copy constructor is private.
      string n = name_;
      do {
        n = "_" + n;
      } while (method.HasLocal(n));
      param_name_ = n;
    }
  }

  protected override string EmitDeclarationType() {
    // For owning parameters the declaration type is the expression type since we can't
    // pass _Own<> by value.
    return type_ is Owning ? type_.EmitExprType() : base.EmitDeclarationType();
  }

  public override string EmitDeclarationName() {
    return param_name_ != null ? param_name_ : name_;
  }

  public void EmitExtraDeclaration(SourceWriter w) {
    if (param_name_ != null)
      w.IWriteLine("{0} {1}({2});", type_.EmitType(), name_, param_name_);
  }
}

class RefOutParameter : Parameter {
  public readonly int mode_;
  public override int GetMode() { return mode_; }

  public RefOutParameter(int mode, TypeExpr type_expr, string name) : base(type_expr, name) {
    mode_ = mode;
    mutable_ = true;
  }

  public override bool CanReceive(Argument a) {
    if (mode_ != a.GetMode())
      return false;
    RefOutArgument ra = (RefOutArgument) a;
    return type_.Equals(ra.StorageType());
  }

  public override GType ReadType() {
    // If a ref parameter has an owning type, we require the user to use the take operator to take
    // ownership of the value, since taking ownership has the visible side effect of clearing the
    // value the ref parameter points to.
    return mode_ == Mode.Ref ? type_.BaseType() : type_;
  }

  public override string Emit() {
    return "(*" + name_ + ")";
  }

  protected override string EmitDeclarationType() {
    return type_.EmitType() + " *";
  }
}

abstract class InlineStatement : Statement {
  public abstract void EmitInline(SourceWriter w);

  public override void Emit(SourceWriter w) {
    EmitInline(w);
    w.WriteLine(";");
  }
}

class VariableDeclaration : InlineStatement {
  ArrayList /* of Local */ locals_ = new ArrayList();

  public VariableDeclaration(TypeExpr type_expr, string name, Expression initializer) {
    locals_.Add(new Local(type_expr, name, initializer));
  }

  public void Add(string name, Expression initializer) {
    TypeExpr t = ((Local) locals_[0]).type_expr_;
    locals_.Add(new Local(t, name, initializer));
  }

  public override bool Check(Context ctx) {
    // local variables are not resolved during the Resolve phase, so we must resolve them here
    foreach (Local l in locals_)
      if (!l.Resolve(ctx.program_) || !l.Check(ctx))
        return false;

    return true;
  }

  // Return the type of all variables in this VariableDeclaration.
  public GType Type() { return ((Local) locals_[0]).Type(); }

  public override GValue Eval(Env env) {
    foreach (Local l in locals_)
      l.EvalInit(env);
    return null;
  }

  void Emit(SourceWriter w, bool in_line) {
    if (in_line)
      Debug.Assert(locals_.Count == 1);

    foreach (Local l in locals_) {
      l.EmitDeclaration(w);
      l.EmitInitializer(w);
      if (!in_line)
        w.WriteLine(";");
    }
  }

  public override void EmitInline(SourceWriter w) { Emit(w, true); }
  public override void Emit(SourceWriter w) { Emit(w, false); }
}

class ExpressionStatement : InlineStatement {
  Expression exp_;

  public ExpressionStatement(Expression e) {
    exp_ = e;
  }

  public override bool Check(Context ctx) {
    if (exp_.CheckTop(ctx) == null)
      return false;
    exp_.SetUnused();
    return true;
  }

  public override GValue Eval(Env env) {
    exp_.Eval(env);   // discard expression value
    return null;
  }

  public override void EmitInline(SourceWriter w) {
    w.Write(exp_.Emit());
  }

}

class If : Statement {
  Expression condition_;
  Statement if_true_;
  Statement if_false_;
  Joiner join_ = new Joiner();

  public If(Expression condition, Statement if_true, Statement if_false) {
    condition_ = condition; if_true_ = if_true; if_false_ = if_false;
  }

  public override bool Check(Context ctx) {
    if (!condition_.Check(ctx, GBool.type_))
      return false;

    Control c = ctx.Prev();

    if (!if_true_.Check(ctx))
      return false;

    join_.Join(ctx.Prev());
    ctx.SetPrev(c);

    if (if_false_ != null && !if_false_.Check(ctx))
      return false;

    join_.Join(ctx.Prev());
    ctx.SetPrev(join_.Combine());
    return true;
  }

  public override GValue Eval(Env env) {
    if (condition_.EvalBool(env))
      return if_true_.Eval(env);
    else
      return if_false_ != null ? if_false_.Eval(env) : null;
  }

  public override void Emit(SourceWriter w) {
    w.Write("if ({0})", condition_.Emit());
    if_true_.EmitEmbedded(w);
    if (if_false_ != null) {
      w.IWrite("else");
      if_false_.EmitEmbedded(w);
    }
  }

}

class DefaultValue : SimpleValue {
  public DefaultValue() { }
  public static readonly DefaultValue instance_ = new DefaultValue();

  public override GType Type()  { Debug.Assert(false); return null; }
  public override string Emit() { Debug.Assert(false); return null; }
}

// A section in a switch statement.
//
// In GEL2, unlike C# and C++, local variables declared in a switch section are visible only
// in that section.  There doesn't seem to be much point in allowing sections to share locals
// since GEL2 (like C#) doesn't allow control to fall through from one section to the next.  This also
// makes compiling to C++ slightly easier since C++ doesn't allow local variables in switch sections
// other than the last to have initializers (see e.g. C++ Primer, 4th ed., 6.6.5 "Variable Definitions
// inside a switch").

class SwitchSection : Node {
  ArrayList /* of Expression */ cases_;     // null represents default:
  public readonly Block block_;

  ArrayList /* of GValue */ values_ = new ArrayList();

  public SwitchSection(ArrayList cases, StatementList statements) {
    cases_ = cases;
    block_ = new Block(statements);
  }

  public bool Check(Context ctx, GType switch_type, Hashtable all_values, out bool is_default) {
    is_default = false;
    foreach (Expression e in cases_) {
      GValue v;
      if (e == null) {
        is_default = true;
        v = new DefaultValue();
      } else {
        if (!e.Check(ctx, switch_type) || !e.CheckConstant())
          return false;
        v = e.Eval(Env.static_, switch_type);
      }
      if (all_values.ContainsKey(v)) {
        Error("switch statement cannot contain the same value twice");
        return false;
      }
      values_.Add(v);
      all_values[v] = null;
    }
    return block_.Check(ctx);
  }

  public bool Match(GValue v) {
    foreach (GValue val in values_)
      if (val.Equals(v))
        return true;
    return false;
  }

  public void Emit(SourceWriter w) {
    foreach (Expression c in cases_)
      if (c == null)
        w.IWriteLine("default:");
      else w.IWriteLine("case {0}:", c.Emit());
    w.Indent();
    block_.Emit(w);
  }

  public void EmitString(SourceWriter w) {
    w.IWrite("if (");
    for (int i = 0 ; i < values_.Count ; ++i) {
      if (i > 0) {
        w.WriteLine(" ||");
        w.IWrite("    ");
      }
      w.Write("_s->_Equals({0})", 
              GString.EmitStringConst(((GString) values_[i]).s_));
    }
    w.Write(") ");
    block_.Emit(w);
  }
}

abstract class Escapable : Scoped {
  public readonly Joiner exit_ = new Joiner();
}

class Switch : Escapable {
  Expression expr_;
  GType type_;
  ArrayList /* of SwitchSection */ sections_;
  SwitchSection default_;    // or null if no default section

  public Switch(Expression expr, ArrayList sections) { expr_ = expr; sections_ = sections; }

  public override bool Check(Context ctx) {
    SetStartVar(ctx);
    type_ = expr_.CheckTop(ctx);
    if (type_ == null)
      return false;
    if (type_ != GInt.type_ && type_ != GChar.type_ && type_ != GString.type_) {
      Error("switch expression must be of type int, char or string");
      return false;
    }
    Context ctx1 = new Context(ctx, this);
    Hashtable values = new Hashtable();
    Control c = ctx1.Prev();
    foreach (SwitchSection s in sections_) {
      bool is_default;
      ctx1.SetPrev(c);
      if (!s.Check(ctx1, type_, values, out is_default))
        return false;
      if (is_default)
        default_ = s;
      if (ctx1.Prev() != unreachable_) {
        s.Error("switch case may not fall through to following case");
        return false;
      }
    }
    if (default_ == null)
      exit_.Join(c);
    ctx1.SetPrev(exit_.Combine());
    return true;
  }

  SwitchSection FindSection(GValue v) {
    foreach (SwitchSection s in sections_)
      if (s.Match(v))
        return s;
    return default_;
  }

  GValue CatchBreak(GValue v) {
    return v is BreakValue ? null : v;
  }

  public override GValue Eval(Env env) {
    GValue v = expr_.Eval(env);
    if (v == null)
      return null;
    SwitchSection s = FindSection(v);
    if (s == null)
      return null;
    return CatchBreak(s.block_.Eval(env));
  }

  public override void Emit(SourceWriter w) {
    if (type_ == GString.type_) {
      // For now, we implement a switch on strings using a sequence of if statements.  We wrap
      // the sequence in a dummy switch statement so that break statements will work properly.
      // If the switch statement has just a default: label then the Microsoft C++ compiler issues
      // a warning, and if it has just a case 0: label then the compiler doesn't realize that the
      // case will always be executed, which may lead to warnings about not all code paths returning
      // a value.  So we use both default: and case 0:, which seems to work fine.
      w.Write("switch (0) ");
      w.OpenBrace();
      w.IWriteLine("case 0:");
      w.IWriteLine("default:");
      w.IWriteLine("StringPtr _s = {0};", expr_.Emit());
      foreach (SwitchSection s in sections_)
        if (s != default_)
          s.EmitString(w);
      if (default_ != null)
        default_.block_.Emit(w);
      w.CloseBrace();
    } else {
      w.Write("switch ({0}) ", expr_.Emit());
      w.OpenBrace();

      foreach (SwitchSection s in sections_)
        s.Emit(w);

      w.CloseBrace();
    }
  }
}

abstract class Loop : Escapable {
  public readonly Joiner loop_ = new Joiner();
}

abstract class ForOrWhile : Loop {
  protected Expression condition_;
  protected Statement statement_;

  protected ForOrWhile(Expression condition, Statement statement) {
    condition_ = condition; statement_ = statement;
  }

  protected abstract InlineStatement Initializer();
  protected abstract InlineStatement Iterator();

  public override bool Check(Context prev_ctx) {
    Context ctx = new Context(prev_ctx, this);   // initializer may declare new local variable
    SetStartVar(ctx);
    if (!Initializer().Check(ctx))
      return false;
    SetTopVar(ctx);

    loop_.AddControl(ctx);

    if (!condition_.CheckTop(ctx, GBool.type_))
      return false;

    if (!condition_.IsTrueLiteral())  // we may exit the loop at this point
      exit_.Join(ctx.Prev());  

    if (!statement_.Check(ctx))
      return false;

    if (!Iterator().Check(ctx))
      return false;

    loop_.Join(ctx.Prev());  // loop back to top

    ctx.SetPrev(exit_.Combine());

    // Add this node to the control graph to destroy any local defined in an initializer above.
    AddControl(ctx);

    return true;
  }

  public override GValue Eval(Env env) {
    env = new Env(env);   // initializer may declare new local
    for (Initializer().Eval(env); condition_.EvalBool(env); Iterator().Eval(env)) {
      GValue v = statement_.Eval(env);
      if (v is BreakValue)
        break;
      if (v is ContinueValue)
        continue;
      if (v != null)
        return v;
    }
    return null;
  }
}

class While : ForOrWhile {
  public While(Expression condition, Statement statement) : base(condition, statement) { }

  protected override InlineStatement Initializer()  { return EmptyStatement.instance_; }
  protected override InlineStatement Iterator()  { return EmptyStatement.instance_; }

  public override void Emit(SourceWriter w) {
    w.Write("while ({0})", condition_.Emit());
    statement_.EmitEmbedded(w);
  }
}

class For : ForOrWhile {
  InlineStatement initializer_;
  InlineStatement iterator_;

  public For(InlineStatement initializer, Expression condition, InlineStatement iterator,
             Statement statement)
    : base(condition != null ? condition : new Literal(new GBool(true)),
           statement) {
    initializer_ = initializer != null ? initializer : new EmptyStatement();
    iterator_ = iterator != null ? iterator : new EmptyStatement();
  }

  protected override InlineStatement Initializer()  { return initializer_; }
  protected override InlineStatement Iterator()  { return iterator_; }

  public override void Emit(SourceWriter w) {
    w.Write("for (");
    initializer_.EmitInline(w);
    w.Write("; {0}; ", condition_.Emit());
    iterator_.EmitInline(w);
    w.Write(")");
    statement_.EmitEmbedded(w);
  }
}

class Do : Loop {
  Statement statement_;
  Expression condition_;

  Joiner join_ = new Joiner();

  public Do(Statement statement, Expression condition) {
    statement_ = statement;
    condition_ = condition;
  }

  public override bool Check(Context ctx) {
    join_.AddControl(ctx);

    Context ctx1 = new Context(ctx, this);
    SetStartVar(ctx);

    if (!statement_.Check(ctx1))
      return false;

    loop_.Join(ctx.Prev());  // continue statements jump here
    ctx.SetPrev(loop_.Combine());

    if (!condition_.CheckTop(ctx, GBool.type_))
      return false;

    if (!condition_.IsFalseLiteral())
      join_.Join(ctx.Prev());   // loop back to top
    if (!condition_.IsTrueLiteral())
      exit_.Join(ctx.Prev());   // fall through to exit
    ctx.SetPrev(exit_.Combine());

    return true;
  }

  public override GValue Eval(Env env) {
    do {
      GValue v = statement_.Eval(env);
      if (v is BreakValue)
        break;
      if (v is ContinueValue)
        continue;
      if (v != null)
        return v;
    } while (condition_.EvalBool(env));
    return null;
  }

  public override void Emit(SourceWriter w) {
    w.IWrite("do");
    statement_.EmitEmbedded(w);
    w.IWriteLine("while ({0});", condition_.Emit());
  }
}

// A helper class for ForEach: a node defining a single variable in the control graph.
class Definer : Node {
  Local local_;

  public Definer(Local local) { local_ = local; }

  public override bool Sets(Local local) {
    return local_ == local;
  }
}

class ForEach : Loop {
  Local local_;
  Expression expr_;
  GType expr_type_;
  Statement statement_;

  Property count_;
  Indexer indexer_;

  Definer definer_;

  public ForEach(TypeExpr type_expr, string name, Expression expr, Statement statement) {
    local_ = new Local(type_expr, name, null);
    expr_ = expr;
    statement_ = statement;
  }

  public override bool Check(Context ctx) {
    if (!local_.Resolve(ctx.program_))
      return false;

    expr_type_ = expr_.CheckTop(ctx);
    if (expr_type_ == null)
      return false;

    count_ = expr_type_.Lookup(this, ctx.class_, false, MemberKind.Property, "Count", null, false) as Property;
    if (count_ == null) {
      Error("object enumerable by foreach must have an accessible property Count");
      return false;
    }
    if (count_.Type() != GInt.type_) {
      Error("object enumerable by foreach must have a property Count of type int");
      return false;
    }

    ArrayList args = new ArrayList();
    args.Add(new InArgument(GInt.type_));
    indexer_ = (Indexer) expr_type_.Lookup(this, ctx.class_, false, MemberKind.Indexer, null, args, false);
    if (indexer_ == null) {
      Error("object enumerable by foreach must have an accessible indexer on integers");
      return false;
    }
    if (!indexer_.CheckAssigning(this, ctx, false))
      return false;

    GType indexer_type = indexer_.Type();
    GType iterator_type = local_.Type();
    if (!indexer_type.CanConvertExplicit(iterator_type, false)) {
      Error("enumeration type {0} is not explicitly convertible to iteration variable type {1}",
        indexer_type, iterator_type);
      return false;
    }

    Context ctx1 = new Context(ctx, this);

    SetStartVar(ctx1);
    if (!local_.Check(ctx1))   // will add local to scope chain
      return false;
    SetTopVar(ctx1);

    // Add a control graph node representing the creation of the local above.
    definer_ = new Definer(local_);
    definer_.AddControl(ctx1);

    loop_.AddControl(ctx1);   // continue statements jump here

    if (!statement_.Check(ctx1))
      return false;

    loop_.Join(ctx1.Prev());   // loop back to top

    exit_.Join(loop_);  // any loop iteration may exit

    ctx1.SetPrev(exit_.Combine());

    // Add this node to the control graph to destroy the local variable created above.
    AddControl(ctx1);

    return true;
  }

  public override GValue Eval(Env env) {
    GValue e = expr_.Eval(env);
    if (e is Null) {
      Error("foreach: can't iterate over null object");
      Gel.Exit();
    }

    int count = ((GInt) count_.Get(e)).i_;

    env = new Env(env);
    env.Add(local_, null);
    for (int i = 0 ; i < count ; ++i) {
      GValue v = indexer_.Get(e, new GInt(i));
      env.Set(local_, v.ConvertExplicit(local_.Type()));
      GValue s = statement_.Eval(env);
      if (s is BreakValue)
        break;
      if (s is ContinueValue)
        continue;
      if (s != null)
        return s;
    }
    return null;
  }

  public override void Emit(SourceWriter w) {
    w.OpenBrace();
    w.IWriteLine("{0} _collection = {1};", expr_type_.BaseType().EmitType(), expr_.Emit());
    w.IWriteLine("int _count = _collection->get_Count();");
    w.IWrite("for (int _i = 0 ; _i < _count ; ++_i) ");
    w.OpenBrace();
    w.Indent();
    local_.EmitDeclaration(w);
    w.WriteLine(" = {0}; ",
      Expression.EmitExplicit(indexer_.Type(), local_.Type(), "_collection->get_Item(_i)", true));
    statement_.EmitInExistingBlock(w);
    w.CloseBrace();
    w.CloseBrace();
  }
}

class BreakValue : GValue {
  public BreakValue() { }

  public static readonly BreakValue instance_ = new BreakValue();

  public override GType Type()  { Debug.Assert(false); return null; }
}

abstract class BreakOrContinue : Scoped {
  protected void Link(Context ctx, Scoped target, Joiner joiner) {
    // When traversed in the control graph, we destroy all variables in the scopes we are exiting,
    // excluding variables defined in the containing Escapable or Loop.
    start_ = target.GetTop();
    SetTopVar(ctx);

    prev_ = ctx.Prev();
    if (prev_ != unreachable_)
      joiner.Join(this);

    ctx.SetPrev(unreachable_);
  }
}

class Break : BreakOrContinue {
  public override bool Check(Context ctx) {
    Escapable e = ctx.escape_;
    if (e == null) {
      Error("break statement must appear inside a while, do, for, foreach or switch statement");
      return false;
    }
    Link(ctx, e, e.exit_);
    return true;
  }

  public override GValue Eval(Env env) {
    return new BreakValue();
  }

  public override void Emit(SourceWriter w) {
    w.WriteLine("break;");
  }
}

class ContinueValue : GValue {
  public ContinueValue() { }

  public static readonly ContinueValue instance_ = new ContinueValue();

  public override GType Type()  { Debug.Assert(false); return null; }
}

class Continue : BreakOrContinue {
  public override bool Check(Context ctx) {
    Loop l = ctx.loop_;
    if (l == null) {
      Error("continue statement must appear inside a while, do, for or foreach statement");
      return false;
    }
    Link(ctx, l, l.loop_);
    return true;
  }

  public override GValue Eval(Env env) {
    return new ContinueValue();
  }

  public override void Emit(SourceWriter w) {
    w.WriteLine("continue;");
  }
}

class Return : Statement {
  Expression exp_;    // null if no return value
  GType exp_type_;
  GType type_;

  public Return(Expression exp) {
    exp_ = exp;
  }

  public override bool Check(Context ctx) {
    if (exp_ == null) {
      if (ctx.method_.ReturnType() != Void.type_) {
        Error("error: returning value from function returning void");
        return false;
      }
    } else {
      type_ = ctx.method_.ReturnType();
      ctx.EnterExpression();
      exp_type_ = exp_.CheckAndHold(ctx);
      if (exp_type_ == null ||
          !exp_type_.CheckConvert(this, type_,
            exp_.IsRefOutParameter() ? ConversionContext.AssignVar : ConversionContext.Other))
        return false;
      exp_.CheckLoseOwnership(exp_type_, type_);
      ctx.FinishExpression();
      exp_.ReleaseRef(ctx);
    }

    ctx.method_.JoinReturn(ctx);
    ctx.SetPrev(unreachable_);
    return true;
  }

  public override GValue Eval(Env env) {
    return exp_ != null ? exp_.Eval(env, type_)
                        : Null.Instance;    // an arbitrary value
  }

  public override void Emit(SourceWriter w) {
    if (exp_ != null && exp_.NeedsRef(exp_type_)) {
      // If exp_ needs a reference count, that reference count needs to survive the destruction
      // of temporaries, so we can't simply emit a temporary reference count wrapper.  For example,
      // when compiling the expression "return (new Foo()).Fun();", if Fun() returns a Foo then we
      // need a wrapper, and we can't generate "return _Ptr<Fun>(Foo().Fun()).Get()" since the _Ptr<Fun>
      // wrapper will be destroyed before the temporary Foo() object is destroyed.  So we need to
      // use an extra variable here.
      w.OpenBrace();
      w.IWriteLine("{0} __ret = {1};", exp_type_.EmitType(), exp_.Emit());
      w.IWriteLine("return __ret.Get();");
      w.CloseBrace();
    } else {
      w.Write("return ");
      if (exp_ != null)
        w.Write(exp_.Emit(exp_type_, type_));
      w.WriteLine(";");
    }
  }
}

class Attribute {
  public const int Abstract = 1,
  Const = 2,
  Extern = 4,
  Override = 8,
  Private = 16,
  Protected = 32,
  Public = 64,
  ReadOnly = 128,
  Ref = 256,
  Static = 512,
  Virtual = 1024,
  Setter = 2048;
}

class AttributeUtil {
  public static bool CheckOnly(int a, int allowed) {
    return (a & ~allowed) == 0;
  }
}

class MethodTraverser : Traverser {
  Method method_;

  public MethodTraverser(Method method) { method_ = method; }

  public override int Handle(Control control) {
    if (control == Control.unreachable_)
      return Cut;

    Node node = control as Node;
    if (node != null) {
      Method c = node.Calls();
      if (c != null)
        method_.calls_.Add(c);
      method_.internal_destroys_.Add(node.NodeDestroys());
    }

    return Continue;
  }
}

class Method : Member {
  public readonly ArrayList /* of Parameter */ parameters_;

  protected Block body_;

  public Joiner exit_ = new Joiner();

  // all parameters and locals defined in this method
  readonly ArrayList /* of Local */ locals_ = new ArrayList();

  ArrayList /* of Method */ overrides_;   // list of all overrides (virtual methods only)

  // methods called from this method
  public readonly ArrayList /* of Method */ calls_ = new ArrayList();

  // Types destroyed inside this method, not including types destroyed through
  // calls to other methods.
  public readonly TypeSet internal_destroys_ = new TypeSet();

  // Types destroyed inside this method or in any methods called by this method.
  TypeSet destroys_;

  // A marker used when performing a depth-first search of the method graph to compute destroys_.
  int method_marker_;

  public Method(int attributes, TypeExpr return_type_expr,
                string name, ArrayList /* of Parameter */ parameters, Block body)
    : base(attributes, return_type_expr, name) {
    parameters_ = parameters;
    body_ = body;
  }

  public override int Kind() { return MemberKind.Method; }

  public GType ReturnType() { return type_; }

  public override ArrayList Parameters() {
    return parameters_;
  }

  public bool IsExtern() { return class_.IsExtern(); }

  public bool IsStatic() {
    return HasAttribute(Attribute.Static);
  }

  public override bool Resolve(Program program) {
    if (!base.Resolve(program))
      return false;
    foreach (Parameter p in parameters_)
      if (!p.Resolve(program))
        return false;
    return true;
  }

  public void AddVar(Local v) {
    locals_.Add(v);
  }

  public override bool Sets(Local local) {
    foreach (Parameter p in parameters_)
      if (p == local && p.GetMode() != Mode.Out)
        return true;
    return false;
  }

  public void JoinReturn(Context ctx) {
    exit_.Join(ctx.Prev());
  }

  public const int kValidAttributes =
    Attribute.Abstract | Attribute.Override |
    Attribute.Private | Attribute.Protected | Attribute.Public |
    Attribute.Static | Attribute.Virtual | Attribute.Setter;

  protected override int ValidAttributes() {
    return kValidAttributes;
  }

  // overridden by Constructor subclass
  protected virtual bool CheckEntry(Context ctx) { return true; }

  // overridden by Constructor subclass
  public override bool Check(Context prev_ctx) {
    if (!base.Check(prev_ctx))
      return false;

    bool abstract_or_extern = HasAttribute(Attribute.Abstract) || IsExtern();

    if (body_.Absent()) {
      if (!abstract_or_extern) {
        Error("a method without a body must be abstract or extern");
        return false;
      }
      return true;
    } 

    if (abstract_or_extern) {
      Error("an abstract or extern method cannot have a body");
      return false;
    }

    Context ctx = new Context(prev_ctx, this);

    prev_ = unreachable_;
    ctx.SetPrev(this);    // begin the control graph with this Method

    foreach (Parameter p in parameters_)
      if (!p.Check(ctx))
        return false;

    if (!CheckEntry(ctx))
      return false;

    if (!body_.Check(ctx))
      return false;

    if (!(this is Constructor) && type_ != Void.type_ && ctx.Prev() != unreachable_) {
      Error("method must return a value");
      return false;
    }
    JoinReturn(ctx);

    ctx.ClearPrev();  // control graph is complete

    // Traverse the control graph to build calls_ and internal_destroys_.
    MethodTraverser mt = new MethodTraverser(this);
    exit_.Traverse(mt, Control.GetMarkerValue());

    bool ok = true;
    foreach (Local v in locals_)    // for all locals and parameters
      ok &= v.CheckUsage(this);

    foreach (Parameter p in parameters_)
      p.CheckParameterUsage(this);

    return ok;
  }

  // Determine whether each local variable needs a reference count.
  protected void ComputeRefs() {
    foreach (Local v in locals_)
      v.ComputeRef(this);
  }

  // Return true if this method has a local or parameter with the given name.
  public bool HasLocal(string name) {
    foreach (Local l in locals_)
      if (l.name_ == name)
        return true;
    return false;
  }

  public override TypeSet NodeDestroys() {
    TypeSet set = new TypeSet();
    foreach (Parameter p in parameters_)
      set.Add(p.Type().VarDestroys());
    return set;
  }

  protected override void AddOverride(Member m) {
    if (overrides_ == null)
      overrides_ = new ArrayList();
    overrides_.Add((Method) m);
  }

  bool Visit(int marker, TypeSet set) {
    if (method_marker_ == marker)
      return true;

    method_marker_ = marker;
    set.Add(internal_destroys_);
    if (set.IsObject())
      return false;   // we already have object; no point in traversing further

    foreach (Method m in calls_)
      if (!m.Visit(marker, set))
        return false;

    if (overrides_ != null)
      foreach (Method m in overrides_)
        if (!m.Visit(marker, set))
          return false;

    return true;
  }

  public TypeSet Destroys() {
    if (destroys_ == null) {
      destroys_ = new TypeSet();
      Visit(Control.GetMarkerValue(), destroys_);
    }
    return destroys_;
  }

  // overridden by Constructor subclass
  public virtual GValue Eval(Env env) {
    return body_.Eval(env);
  }

  public GValue Invoke(GValue obj, ArrayList /* of ValueOrLocation */ values) {
    if (body_.Absent()) { // an external method
      ValueList list = new ValueList(values);
      if (IsStatic())
        return class_.InvokeStatic(this, list);   // let the class handle it
      return obj.Invoke(this, list);   // let the object handle it
    }

    Env env = new Env(obj);
    Debug.Assert(values.Count == parameters_.Count);
    for (int i = 0 ; i < values.Count ; ++i)
      env.Add((Parameter) parameters_[i], (ValueOrLocation) values[i]);

    return Eval(env);
  }

  protected void EmitParameterNames(SourceWriter w) {
    w.Write("(");
    for (int i = 0; i < parameters_.Count; ++i) {
      if (i > 0)
        w.Write(", ");
      w.Write(Param(i).EmitDeclarationName());
    }
    w.Write(")");
  }

  protected void EmitParameters(SourceWriter w) {
    w.Write("(");
    for (int i = 0; i < parameters_.Count; ++i) {
      if (i > 0)
        w.Write(", ");
      Param(i).EmitDeclaration(w);
    }
    w.Write(") ");
  }

  void EmitAttributes(SourceWriter w, bool declaration) {
    w.Indent();
    if (!IsStatic() && !IsVirtual())
      return;

    if (!declaration)
      w.Write("/* ");
    if (IsStatic())
      w.Write("static ");
    if (IsVirtual())
      w.Write("virtual ");
    if (!declaration)
      w.Write("*/ ");
  }

  void EmitSignature(SourceWriter w, bool declaration) {
    EmitAttributes(w, declaration);
    w.Write("{0} ", type_.EmitReturnType());
    if (!declaration)
      w.Write("{0}::", class_.name_);
    if (HasAttribute(Attribute.Setter))
      w.Write("_");
    w.Write(name_);
    EmitParameters(w);
  }

  public virtual void EmitDeclaration(SourceWriter w) {
    ComputeRefs();
    EmitSignature(w, true);
    if (body_.Absent())
      w.Write("= 0");
    w.WriteLine(";");

    if (HasAttribute(Attribute.Setter)) {
      w.WriteLine("");
      EmitAttributes(w, true);
      w.Write("{0} ", Param(parameters_.Count - 1).Type().EmitType());
      w.Write(name_);
      EmitParameters(w);
      w.OpenBrace();
      w.IWrite("_{0}", name_);
      EmitParameterNames(w);
      w.WriteLine(";");
      w.IWriteLine("return value;");
      w.CloseBrace();
    }
  }

  protected void EmitExtraDeclarations(SourceWriter w) {
    foreach (Parameter p in parameters_)
      p.EmitExtraDeclaration(w);
  }

  public virtual void Emit(SourceWriter w) {
    if (!body_.Absent()) {
      EmitSignature(w, false);
      w.OpenBrace();
      EmitExtraDeclarations(w);
      body_.list_.Emit(w);
      w.CloseBrace();
      w.WriteLine("");
    }
    if (Gel.print_type_sets_)
      Console.WriteLine("{0}.{1}: {2}", class_.name_, name_, Destroys());
  }
}

class Constructor : Method {
  bool call_base_;
  ArrayList /* of Argument */ initializer_params_;

  Constructor initializer_;

  bool invoked_;  // true if another constructor invokes this one using : base() or : this()

  public Constructor(int attributes, string name, ArrayList parameters,
                     bool call_base, ArrayList initializer_params, Block body)
    : base(attributes, null, name, parameters, body) {
    call_base_ = call_base;
    initializer_params_ = initializer_params;
  }

  public Constructor(int attributes, string name, ArrayList parameters, Block body)
    : this(attributes, name, parameters, true, new ArrayList(), body) { }

  public override int Kind() { return MemberKind.Constructor; }

  protected override int ValidAttributes() {
    return Attribute.Private | Attribute.Protected | Attribute.Public;
  }

  public override bool Check(Context ctx) {
    if (name_ != class_.name_) {
      Error("constructor name must match class name");
      return false;
    }
    return base.Check(ctx);
  }

  protected override bool CheckEntry(Context ctx) {
    Class c = call_base_ ? class_.Parent() : class_;
    if (c != null || initializer_params_.Count > 0) {
      initializer_ = (Constructor) Invocation.CheckInvoke(this, ctx, call_base_, c,
                                      c.name_, initializer_params_, MemberKind.Constructor);
      if (initializer_ == null)
        return false;
      if (initializer_ == this) {
        Error("constructor initializer invokes itself");
        return false;
      }
      initializer_.invoked_ = true;
    }
    return true;
  }

  // A Constructor node in the control graph represents the call to the base constructor;
  // this node gets added when we call Invocation.CheckInvoke() above.
  public override Method Calls() { return initializer_; }

  public override GValue Eval(Env env) {
    if (initializer_ == null || initializer_.class_ != class_) {
      // run instance variable initializers
      foreach (Field f in class_.fields_)
        if (!f.IsConstOrStatic() && f.Initializer() != null)
          ((GObject) env.this_).Set(f, f.Initializer().Eval(Env.static_, f.Type()));
    }
    if (initializer_ != null)
      Invocation.CallMethod(env, env.this_, initializer_, initializer_params_, false);
    return body_.Eval(env);
  }

  void EmitInitializerArgs(SourceWriter w) {
    w.WriteLine("({0});", Invocation.EmitArguments(initializer_, initializer_params_));
  }

  void EmitConstructorBody(SourceWriter w) {
    w.OpenBrace();
    EmitExtraDeclarations(w);
    if (call_base_) {
      if (class_.constructors_.Count > 1)
        w.IWriteLine("_Init();");
      else class_.EmitInitializers(w);

      Class parent = class_.Parent();
      if (parent != GObject.type_) {
        w.IWrite("{0}::_Construct", parent.name_);
        EmitInitializerArgs(w);
      }
    } else {
      w.IWrite("_Construct");
      EmitInitializerArgs(w);
    }

    body_.list_.Emit(w);
    w.CloseBrace();
  }

  public override void EmitDeclaration(SourceWriter w) {
    ComputeRefs();

    // If we're invoked by some other constructor then we need to declare a _Construct
    // method which that constructor can call.
    if (invoked_) {
      w.IWrite("void _Construct");
      EmitParameters(w);
      w.WriteLine(";");
    }

    // Now declare the actual C++ constructor.
    w.IWrite(name_);
    EmitParameters(w);
    w.WriteLine(";");
  }

  public override void Emit(SourceWriter w) {
    // If we're invoked by some other constructor then we need to emit a _Construct
    // method which that constructor can call.
    if (invoked_) {
      w.IWrite("void {0}::_Construct", name_);
      EmitParameters(w);
      EmitConstructorBody(w);
      w.WriteLine("");
    }

    // Now emit the actual C++ constructor.
    w.IWrite("{0}::{1}", name_, name_);
    EmitParameters(w);
    Class parent = class_.Parent();
    if (parent != GObject.type_)
      w.Write(": {0}((Dummy *) 0) ", parent.name_);

    if (invoked_) {   // call the _Construct method we just generated
      w.Write("{ _Construct");
      EmitParameterNames(w);
      w.WriteLine("; }");
    } else EmitConstructorBody(w);
    w.WriteLine("");
  }
}

abstract class PropertyOrIndexer : LMember {
  // If a get accessor is not declared at all, then get_block_ will be null; if it is
  // declared, but has an empty body ("get;") then get_block_ will be a Block whose list_ is
  // null.  We need to distinguish these cases since for abstract or extern properties the
  // presence of a get accessor with an empty body indicates that a property is readable.
  // (set_block_ works similarly.)
  Block get_block_, set_block_;

  protected Method getter_, setter_;

  protected PropertyOrIndexer(int attributes, TypeExpr type_expr, string name,
                              string id1, Block block1, string id2, Block block2)
    : base(attributes, type_expr, name) {
    StoreAccessor(id1, block1);
    if (id2 != null)
      StoreAccessor(id2, block2);
    if (id1 == id2)
      Error("can't have two {0} accessors", id1);
  }

  void StoreAccessor(string id, Block block) {
    // Note that we don't have scanner tokens GET and SET since these are not keywords in GEL2.
    switch (id) {
      case "get": get_block_ = block; return;
      case "set": set_block_ = block; return;
    }
    Error("expected get or set");
  }

  public Method Getter() { return getter_; }
  public Method Setter() { return setter_; }

  protected abstract string BaseName();

  public override bool Check(Context ctx) {
    if (!base.Check(ctx))
      return false;

    if (get_block_ != null) {
      getter_ = new Method(attributes_, new TypeLiteral(type_), "get_" + BaseName(), Parameters(), get_block_);
      class_.Add(getter_);
      if (!getter_.Resolve(ctx.program_) || !getter_.Check(ctx))
        return false;
    }

    if (set_block_ != null) {
      ArrayList set_params = new ArrayList();
      foreach (Parameter p in Parameters())
        set_params.Add(p);
      set_params.Add(new Parameter(new TypeLiteral(type_), "value"));
      setter_ = new Method(attributes_ | Attribute.Setter,
                           new TypeLiteral(Void.type_), "set_" + BaseName(), set_params, set_block_);
      class_.Add(setter_);
      if (!setter_.Resolve(ctx.program_) || !setter_.Check(ctx))
        return false;
    }

    return true;
  }

  public override bool CheckAssigning(Syntax caller, Context ctx, bool assigning) {
    if (assigning && set_block_ == null) {
      caller.Error("can't assign to read-only {0}", KindString());
      return false;
    }
    if (!assigning && get_block_ == null) {
      caller.Error("can't read from write-only {0}", KindString());
      return false;
    }
    return true;
  }

  public override Location GetLocation(GObject obj) { Debug.Assert(false); return null; }
}

class Property : PropertyOrIndexer {
  public Property(int attributes, TypeExpr type_expr, string name,
                  string id1, Block block1, string id2, Block block2)
    : base(attributes, type_expr, name, id1, block1, id2, block2) { }

  public override int Kind() { return MemberKind.Property; }

  protected override int ValidAttributes() {
    return Method.kValidAttributes;
  }

  protected override string BaseName() { return name_; }

  public override GValue Get(GValue obj) {
    return Invocation.InvokeMethod(obj, getter_, new ArrayList(), true);
  }

  public override void Set(GObject obj, GValue val) {
    ArrayList a = new ArrayList();
    a.Add(val);
    Invocation.InvokeMethod(obj, setter_, a, true);
  }

  public override string Emit() {
    return String.Format("get_{0}()", name_);
  }

  public override string EmitSet(string val) {
    return String.Format("set_{0}({1})", name_, val);
  }
}

class Indexer : PropertyOrIndexer {
  public readonly Parameter parameter_;

  ArrayList /* of Parameter */ parameters_;

  public Indexer(int attributes, TypeExpr type_expr, Parameter parameter,
                 string id1, Block block1, string id2, Block block2)
    : base(attributes, type_expr, null, id1, block1, id2, block2) {
    parameter_ = parameter;

    parameters_ = new ArrayList();
    parameters_.Add(parameter);
  }

  public override int Kind() { return MemberKind.Indexer; }

  public override bool Resolve(Program program) {
    return base.Resolve(program) && parameter_.Resolve(program);
  }

  protected override int ValidAttributes() {
    return Attribute.Abstract | Attribute.Override |
    Attribute.Private | Attribute.Protected | Attribute.Public | Attribute.Virtual;
  }

  protected override string BaseName() { return "Item"; }

  public override bool Check(Context ctx) {
    if (parameter_ is RefOutParameter) {
      Error("indexer parameter may not be ref or out");
      return false;
    }

    return base.Check(ctx);
  }

  public override ArrayList /* of Parameter */ Parameters() {
    return parameters_; 
  }

  public GValue Get(GValue obj, GValue index) {
    ArrayList a = new ArrayList();
    a.Add(index);
    return Invocation.InvokeMethod(obj, getter_, a, true);
  }

  public void Set(GObject obj, GValue index, GValue val) {
    ArrayList a = new ArrayList();
    a.Add(index);
    a.Add(val);
    Invocation.InvokeMethod(obj, setter_, a, true);
  }
}

class Class : GType {
  Syntax syntax_ = new Syntax();
  Program program_;   // containing program
  int attributes_;
  public readonly string name_;
  string parent_name_;    // or null if not specified

  Class parent_;

  public readonly ArrayList /* of Field */ fields_ = new ArrayList();
  public readonly ArrayList /* of Method */ methods_ = new ArrayList();
  public readonly ArrayList /* of Property */ properties_ = new ArrayList();
  public readonly ArrayList /* of Indexer */ indexers_ = new ArrayList();
  public readonly ArrayList /* of Constructor */ constructors_ = new ArrayList();

  public readonly ArrayList /* of Member */ members_ = new ArrayList();

  public readonly ArrayList /* of Class */ subclasses_ = new ArrayList();
  bool emitted_;

  // If virtual_ is true, then this class needs a vtable; we set this for a class C
  // in any of the following cases:
  // - the program converts some type T to C ^ (for then a C ^ may hold a T, and when
  // the owning pointer is destroyed we need to know which destructor to call)
  // - the program explicitly converts from C to some other type (so we need RTTI for C)
  bool virtual_;
  
  // If object_inherit_ is true, then this class must derive from Object in generated C++
  // code; we set this for a class C in any of the following cases:
  // - a member lookup in C yields a member in Object
  // - the program converts from C to Object or from Object to C
  // - the class is used in a generic context, i.e. in the type C [] or C ^ [].  All such types
  //   use the generic compiled classes Array<Object> / Array<_Own<Object>>, and so in this case we need
  //   to be able to cast between C and Object.
  //
  // If object_inherit_ is true then this class and all its superclasses will have vtables since
  // the C++ Object class has a vtable.
  bool object_inherit_;

  bool need_destroy_;  // true if we need to emit _Destroy methods for this class

  // The set of types which may be destroyed when an instance of this class is destroyed.
  TypeSet destroys_;

  // A marker used in performing a depth-first search to construct the destroys_ type set.
  int marker_;

  protected Class(string name) { name_ = name; }

  public static Class New(int attributes, string name, string parent_name) {
    Class c = Internal.Find(name);
    if (c == null)
      c = new Class(name);

    c.attributes_ = attributes;
    c.parent_name_ = parent_name;

    return c;
  }

  public override bool IsOwned() { return true; }

  public Program GetProgram() { return program_; }
  public void SetProgram(Program p) { program_ = p; }

  public bool IsExtern() { return HasAttribute(Attribute.Extern); }

  public override Class Parent() { return parent_; }

  public override string ToString() { return name_; }

  public override ArrayList Members() { return members_; }

  public override SimpleValue DefaultValue() { return Null.Instance; }

  public override void SetVirtual() { virtual_ = true; }
  public override void SetObjectInherit() { object_inherit_ = true;  }
  public void NeedDestroy() { need_destroy_ = true; }

  public bool HasAttribute(int a) {
    return ((attributes_ & a) != 0);
  }

  public virtual GValue New() { return new GObject(this); }
  public virtual GValue InvokeStatic(Method m, ValueList args) { Debug.Assert(false); return null; }

  public void Add(Field f) { f.SetClass(this); fields_.Add(f); members_.Add(f);  }

  public virtual void Add(Method m) { m.SetClass(this); methods_.Add(m); members_.Add(m);  }

  public void Add(Property p) { p.SetClass(this); properties_.Add(p); members_.Add(p);  }

  public void Add(Indexer i) { i.SetClass(this); indexers_.Add(i); members_.Add(i); }

  public void AddConstructor(Constructor c) { c.SetClass(this); constructors_.Add(c); members_.Add(c);  }

  public ArrayList /* of Member */ FindAbstractMembers() {
    ArrayList /* of Member */ a;
    if (parent_ != null)  {
      a = parent_.FindAbstractMembers();

      // remove members overridden in this class
      int i = 0;
      while (i < a.Count) {
        Member m = (Member) a[i];
        Member n = GetMatchingMember(m);
        if (n != null) {
          Debug.Assert(n.HasAttribute(Attribute.Abstract | Attribute.Override));
          a.RemoveAt(i);
        } else ++i;
      }
    } else a = new ArrayList();

    // add abstract members from this class
    foreach (Member m in members_) {
      if (m.HasAttribute(Attribute.Abstract))
        a.Add(m);
    }

    return a;
  }

  public bool ResolveAll(Program program) {
    if (parent_name_ != null) {
      parent_ = program.FindClass(parent_name_);
      if (parent_ == null) {
        syntax_.Error("can't find parent class {0}", parent_name_);
        return false;
      }
    } else if (this == GObject.type_)
      parent_ = null;
    else parent_ = GObject.type_;
    if (parent_ != null)
      parent_.subclasses_.Add(this);

    foreach (Member m in members_)
      if (!m.Resolve(program))
        return false;

    if (!IsExtern() && constructors_.Count == 0)
      // add a default constructor
      AddConstructor(new Constructor(Attribute.Public, name_, new ArrayList(), Block.EmptyBlock()));

    return true;
  }

  // In our first checking pass we check all constant fields since we may need to evaluate them
  // in the course of checking other code.
  public bool Check1(Context prev_ctx) {
    if (!AttributeUtil.CheckOnly(attributes_,
         Attribute.Abstract | Attribute.Extern | Attribute.Public)) {
      syntax_.Error("illegal class attribute");
      return false;
    }

    if (prev_ctx.program_.FindClass(name_) != this) {
      syntax_.Error("can't have two classes named {0}", name_);
      return false;
    }

    Context ctx = new Context(prev_ctx, this);
    bool ok = true;
    foreach (Field f in fields_) {
      ConstField cf = f as ConstField;
      if (cf != null)
        ok &= cf.Check(ctx);
    }
    return ok;
  }

  public bool Check(Context prev_ctx) {
    Context ctx = new Context(prev_ctx, this);

    bool ok = true;

    foreach (Member m in members_)
      if (m is Field && !(m is ConstField) || m is Method)
        ok &= m.Check(ctx);

    // We need to check properties and indexers separately since they add methods to the members_
    // array as they are checked.
    foreach (Property p in properties_)
      ok &= p.Check(ctx);
    foreach (Indexer i in indexers_)
      ok &= i.Check(ctx);

    if (!HasAttribute(Attribute.Abstract)) {
      ArrayList a = FindAbstractMembers();
      if (a.Count > 0) {
        foreach (Member m in a)
          syntax_.Error("class does not define inherited abstract {0} {1}", m.KindString(), m.name_);
        ok = false;
      }
    }

    return ok;
  }

  public void GetMainMethod(ArrayList /* of Method */ result) {
    foreach (Method m in methods_)
      if (m.name_ == "Main" && m.IsPublic() && m.IsStatic() && m.ReturnType() == Void.type_) {
        int c = m.parameters_.Count;
        if (c > 1)
          continue;
        if (c == 1) {
          Parameter p = m.Param(0);
          if (p.GetMode() != 0 || !p.Type().Equals(new ArrayType(GString.type_)) )
            continue;
        }
        result.Add(m);
      }
  }

  public override void FindTypeDestroys(int marker, TypeSet set) {
    if (marker_ == marker)
      return;
    marker_ = marker;
    set.Add(this);
    for (Class c = this; c != null; c = c.parent_) {
      if (c != this && c.marker_ == marker)
        break;
      foreach (Field f in c.fields_)
        if (!f.IsConstOrStatic())
          f.Type().FindVarDestroys(marker, set);
    }
    foreach (Class c in subclasses_)
      c.FindTypeDestroys(marker, set);
  }

  public override TypeSet TypeDestroys() {
    if (destroys_ == null) {
      destroys_ = new TypeSet();
      FindTypeDestroys(Control.GetMarkerValue(), destroys_);
    }
    return destroys_;
  }

  public void StaticInit() {
    foreach (Field f in fields_) {
      StaticField sf = f as StaticField;
      if (sf != null)
        sf.Init();
    }
  }

  public override string EmitTypeName() { return name_; }

  int EmitAccess(SourceWriter w, int old_access, int new_access) {
    new_access = (new_access & Attribute.Public) != 0 ? Attribute.Public : Attribute.Protected;

    if (new_access != old_access) {
      switch (new_access) {
        case Attribute.Protected:
          w.Indent(-1);
          w.WriteLine("protected:");
          break;
        case Attribute.Public:
          w.Indent(-1);
          w.WriteLine("public:");
          break;
        default:
          Debug.Assert(false);
          break;
      }
    }

    return new_access;
  }

  public void EmitInitializers(SourceWriter w) {
    foreach (Field f in fields_)
      if (!f.IsConstOrStatic())
        f.EmitInitializer(w);
  }

  // Return true if this top-level class must inherit from Object.
  bool ObjectInherit() {
    if (object_inherit_)
      return true;
    foreach (Class c in subclasses_)
      if (c.ObjectInherit())
        return true;
    return false;
  }

  public void EmitDeclaration(SourceWriter w) {
    if (emitted_ || IsExtern())
      return;

    // C++ requires a parent class to appear before its subclasses in a source file.
    parent_.EmitDeclaration(w);

    emitted_ = true;

    w.Write("class {0} ", name_);
    if (parent_ != null)
      w.Write(": public {0} ",
              parent_ == GObject.type_ && !ObjectInherit() ? "_Object" : parent_.name_);
    w.OpenBrace();

    int access = 0;

    if (fields_.Count > 0) {
      foreach (Field f in fields_) {
        access = EmitAccess(w, access, f.attributes_);
        f.EmitDeclaration(w);
      }
      w.WriteLine("");
    }

    if (subclasses_.Count > 0) {
      access = EmitAccess(w, access, Attribute.Protected);
      w.IWrite("{0}(Dummy *dummy) ", name_);
      if (parent_ != GObject.type_)
        w.Write(": {0}(dummy) ", parent_.name_);
      w.WriteLine("{ }");
      w.WriteLine("");
    }

    // If we have more than one constructor, emit an _Init() method which constructors can
    // call to initialize instance variables.
    if (constructors_.Count > 1) {
      access = EmitAccess(w, access, Attribute.Protected);
      w.IWriteLine("void _Init();");
      w.WriteLine("");
    }

    foreach (Constructor c in constructors_) {
      access = EmitAccess(w, access, c.attributes_);
      c.EmitDeclaration(w);
    }

    if (virtual_) {
      access = EmitAccess(w, access, Attribute.Public);
      w.IWrite("virtual ~{0}()", name_);
      w.WriteLine(" { }");
      w.WriteLine("");
    }

    if (need_destroy_) {
      access = EmitAccess(w, access, Attribute.Public);
      w.IWriteLine("GEL_OBJECT({0})", name_);
      w.WriteLine("");
    }

    foreach (Method m in methods_) {
      access = EmitAccess(w, access, m.attributes_);
      m.EmitDeclaration(w);
    }

    w.SubIndent();
    w.WriteLine("};");
    w.WriteLine("");
  }

  public void Emit(SourceWriter w) {
    if (IsExtern())
      return;

    if (fields_.Count > 0) {
      foreach (Field f in fields_)
        f.Emit(w);
      w.WriteLine("");
    }

    if (constructors_.Count > 1) {
      w.IWrite("void {0}::_Init() ", name_);
      w.OpenBrace();
      EmitInitializers(w);
      w.CloseBrace();
      w.WriteLine("");
    }

    foreach (Constructor c in constructors_)
      c.Emit(w);

    foreach (Method m in methods_)
      m.Emit(w);

    if (Gel.print_type_sets_)
      Console.WriteLine("~{0}: {1}", name_, TypeDestroys());
  }
}

class ValueList {
  public ArrayList list_;
  int index_ = 0;

  public ValueList(ArrayList list) { list_ = list; }

  public GValue Object() { return (GValue) list_[index_++]; }
  public bool Bool() { return ((GBool) list_[index_++]).b_; }
  public int Int() { return ((GInt) list_[index_++]).i_; }
  public char Char() { return ((GChar) list_[index_++]).c_; }
  public string GetString() { return ((GString) list_[index_++]).s_; }
}

class Internal : Class {
  static ArrayList /* of Internal */ all_ = new ArrayList();

  protected Internal(string name) : base(name) { }

  public static Internal Find(string name) {
    foreach (Internal p in all_)
      if (p.name_ == name)
        return p;
    return null;
  }

  static void Add(Internal p) { all_.Add(p); }

  public static void Init() {
    Add(GObject.type_);
    Add(GArray.array_class_);
    Add(GBool.type_);
    Add(GChar.type_);
    Add(GDouble.type_);
    Add(GFloat.type_);
    Add(GInt.type_);
    Add(GString.type_);
    Add(GStringBuilder.type_);
    Add(PoolClass.instance_);
    Add(DebugClass.instance_);
    Add(EnvironmentClass.instance_);

    Add(ConsoleClass.instance_);
    Add(FileClass.instance_);
    Add(PathClass.instance_);
    Add(GStreamReader.type_);
  }
}

class ObjectClass : Internal {
  public Method equals_;
  public Method get_hash_code_;
  public Method to_string_;

  public ObjectClass() : base("Object") { }

  public override void Add(Method m) {
    switch (m.name_) {
      case "Equals": equals_ = m; break;
      case "GetHashCode": get_hash_code_ = m; break;
      case "ToString": to_string_ = m; break;
    }
    base.Add(m);
  }
}

class ArrayClass : Internal {
  public ArrayClass() : base("Array") { }
}

abstract class SimpleType : Internal {
  protected SimpleType(string name) : base(name) { }

  public override bool IsOwned() { return false; }
  public override bool IsReference() { return false; }
  public override bool IsValue() { return true; }

  public override string EmitExprType() { return EmitType(); }
  public override string EmitType() { Debug.Assert(false); return null; }
}

abstract class IntegralType : SimpleType {
  protected IntegralType(string name) : base(name) { }
}

class BoolClass : IntegralType {
  public BoolClass() : base("Bool") { }

  static GBool default_ = new GBool(false);
  public override SimpleValue DefaultValue() { return default_; }

  public override string ToString() { return "bool"; }

  public override string EmitType() { return "bool"; }
}

class CharClass : IntegralType {
  public CharClass() : base("Char") { }

  static GChar default_ = new GChar('\0');

  public override SimpleValue DefaultValue() { return default_; }

  public override bool CanConvert1(GType t) { return t == GInt.type_; }

  public override string ToString() { return "char"; }

  public override GValue InvokeStatic(Method m, ValueList args) {
    switch (m.name_) {
      case "IsDigit": return new GBool(Char.IsDigit(args.Char()));
      case "IsLetter": return new GBool(Char.IsLetter(args.Char()));
      case "IsWhiteSpace": return new GBool(Char.IsWhiteSpace(args.Char()));
      default: Debug.Assert(false); return null;
    }
  }

  public override string EmitType() { return "wchar_t"; }
}

class IntClass : IntegralType {
  public IntClass() : base("Int") { }

  static GInt default_ = new GInt(0);

  public override SimpleValue DefaultValue() { return default_; }

  public override bool CanConvert1(GType t) {
    return t == GFloat.type_ || t == GDouble.type_;
  }

  protected override bool CanConvertExplicit1(GType t) {
    return t == GChar.type_;
  }

  public override string ToString() { return "int"; }

  public override GValue InvokeStatic(Method m, ValueList args) {
    switch (m.name_) {
      case "Parse": return new GInt(int.Parse(args.GetString()));
      default: Debug.Assert(false); return null;
    }
  }

  public override string EmitType() { return "int"; }
}

class FloatClass : SimpleType {
  public FloatClass() : base("Single") { }

  static GFloat default_ = new GFloat(0.0f);

  public override SimpleValue DefaultValue() { return default_; }

  public override bool CanConvert1(GType t) {
    return t == GDouble.type_;
  }

  protected override bool CanConvertExplicit1(GType t) {
    return t == GInt.type_;
  }

  public override string ToString() { return "float"; }

  public override string EmitType() { return "float"; }
}

class DoubleClass : SimpleType {
  public DoubleClass() : base("Double") { }

  static GDouble default_ = new GDouble(0.0d);

  public override SimpleValue DefaultValue() { return default_; }

  protected override bool CanConvertExplicit1(GType t) {
    return t == GInt.type_ || t == GFloat.type_;
  }

  public override string ToString() { return "double"; }

  public override string EmitType() { return "double"; }
}

class StringClass : Internal {
  public StringClass() : base("String") { }

  public override bool IsOwned() { return false; }
  public override bool IsValue() { return true; }

  public override string ToString() { return "string"; }

  public override string EmitType() {
    return "_Ref<String>";
  }

  public override string EmitReturnType() {
    return EmitType();
  }

  public override string EmitGenericType() {
    return EmitType();
  }

  public override GValue InvokeStatic(Method m, ValueList args) {
    switch (m.name_) {
      case "Format": return new GString(String.Format(args.GetString(), args.Object()));
      default: Debug.Assert(false); return null;
    }
  }
}

class StringBuilderClass : Internal {
  public StringBuilderClass() : base("StringBuilder") { }
  public override GValue New() { return new GStringBuilder(); }
}

class GStringBuilder : GValue {
  readonly StringBuilder b_ = new StringBuilder();

  public static readonly StringBuilderClass type_ = new StringBuilderClass();

  public override GType Type() { return type_; }

  public override GValue Invoke(Method m, ValueList args) {
    if (m.GetClass() != type_)
      return base.Invoke(m, args);
    switch (m.name_) {
      case "Append": b_.Append(args.Char()); return null;
      case "ToString": return new GString(b_.ToString());
      default: Debug.Assert(false); return null;
    }
  }
}

class PoolClass : Internal {
  public PoolClass() : base("Pool") { }

  public static PoolClass instance_ = new PoolClass();
}

class DebugClass : Internal {
  public DebugClass() : base("Debug") { }
  public static readonly DebugClass instance_ = new DebugClass();
  
  public override GValue InvokeStatic(Method m, ValueList args) {
    switch (m.name_) {
      case "Assert": Debug.Assert(args.Bool()); return null;
      default: Debug.Assert(false); return null;
    }
  }
}

class EnvironmentClass : Internal {
  public EnvironmentClass() : base("Environment") { }
  public static readonly EnvironmentClass instance_ = new EnvironmentClass();

  public override GValue InvokeStatic(Method m, ValueList args) {
    switch (m.name_) {
      case "Exit": Environment.Exit(args.Int()); return null;
      default: Debug.Assert(false); return null;
    }
  }
}

// built-in I/O classes

class ConsoleClass : Internal {
  public ConsoleClass() : base("Console") { }

  public override GValue InvokeStatic(Method m, ValueList args) {
    switch (m.name_) {
      case "Write":
        switch (m.parameters_.Count) {
          case 1: Console.Write(args.Object()); return null;
          case 2: Console.Write(args.GetString(), args.Object()); return null;
          case 3: Console.Write(args.GetString(), args.Object(), args.Object()); return null;
          default: Debug.Assert(false); return null;
        }
      case "WriteLine":
        switch (m.parameters_.Count) {
          case 1: Console.WriteLine(args.Object()); return null;
          case 2: Console.WriteLine(args.GetString(), args.Object()); return null;
          case 3: Console.WriteLine(args.GetString(), args.Object(), args.Object()); return null;
          default: Debug.Assert(false); return null;
        }
      default: Debug.Assert(false); return null;
    }
  }

  public static readonly ConsoleClass instance_ = new ConsoleClass();
}

class FileClass : Internal {
  public FileClass() : base("File") { }

  public override GValue InvokeStatic(Method m, ValueList args) {
    switch (m.name_) {
      case "Delete": File.Delete(args.GetString()); return null;
      case "Exists": return new GBool(File.Exists(args.ToString()));
      default: Debug.Assert(false); return null;
    }
  }

  public static readonly FileClass instance_ = new FileClass();
}

class PathClass : Internal {
  public PathClass() : base("Path") { }

  public override GValue InvokeStatic(Method m, ValueList args) {
    switch (m.name_) {
      case "GetTempFileName": return new GString(Path.GetTempFileName());
      default: Debug.Assert(false); return null;
    }
  }

  public static readonly PathClass instance_ = new PathClass();
}

class StreamReaderClass : Internal {
  public StreamReaderClass() : base("StreamReader") { }
  public override GValue New() { return new GStreamReader(); }
}

class GStreamReader : GValue {
  StreamReader reader_;

  public static readonly StreamReaderClass type_ = new StreamReaderClass();

  public override GType Type() { return type_; }

  public override GValue Invoke(Method m, ValueList args) {
    if (m.GetClass() != type_)
      return base.Invoke(m, args);
    switch (m.name_) {
      case "StreamReader": reader_ = new StreamReader(args.GetString()); return null;
      case "Read": return new GInt(reader_.Read());
      case "Peek": return new GInt(reader_.Peek());
      default: Debug.Assert(false); return null;
    }
  }
}

class Program {
  ArrayList /* of string */ gel_import_ = new ArrayList();
  ArrayList /* of string */ cpp_import_ = new ArrayList();

  static Scanner scanner_;

  ArrayList classes_ = new ArrayList();

  public bool crt_alloc_;
  public bool debug_;
  public bool safe_ = true;
  
  public bool profile_ref_;
  
  public Control prev_;   // previous node in control flow graph, used during graph construction

  public void Import(string s) {
    ArrayList a = null;
    string extension = Path.GetExtension(s);
    switch (extension) {
      case ".gel": a = gel_import_; break;
      case ".cpp": a = cpp_import_; break;
      default:
        new Syntax().Error("can't import file with extension {0}", extension);
        Environment.Exit(0);
        break;
    }
    foreach (string i in a)
      if (i == s)
        return;   // already imported
    a.Add(s);
  }

  public void FindAndImport(string s) {
    // First look relative to the importing file.
    string dir = Path.GetDirectoryName(Gel.CurrentFile());
    string s1 = Path.Combine(dir, s);
    if (!File.Exists(s1)) {
      // Now look relative to the GEL2 directory.
      s1 = Path.Combine(Gel.gel_directory_, s);
      if (!File.Exists(s1)) {
        new Syntax().Error("import not found: ", s);
        Environment.Exit(0);
      }
    }
    Import(s1);
  }
  
  public void Add(Class c) {
    c.SetProgram(this);
    classes_.Add(c);
  }

  public Class FindClass(string name) {
    foreach (Class c in classes_)
      if (c.name_ == name)
        return c;
    return null;
  }

  public string CurrentFile() {
    return scanner_.filename_;
  }

  public int Line() {
    return scanner_.Line();
  }

  string Builtin(string module) {
    string file = String.Format("{0}.gel", module);
    return Path.Combine(Gel.gel_directory_, file);
  }

  public bool Parse() {
    Parser parser = new Parser();
    for (int i = 0; i < gel_import_.Count; ++i) {
      string file = (string)gel_import_[i];
      scanner_ = new Scanner(file);
      parser.yyParse(scanner_);
      if (i == 0) {
        Import(Builtin("internal"));
        Import(Builtin("library"));
      }
    }
    return true;
  }

  public bool Resolve() {
    foreach (Class c in classes_)
      if (!c.ResolveAll(this))
        return false;

    return true;
  }

  public bool Check() {
    Context ctx = new Context(this);
    bool ok = true;

    // Make a first checking pass where we check only constant fields.
    foreach (Class c in classes_)
      ok &= c.Check1(ctx);

    foreach (Class c in classes_)
      ok &= c.Check(ctx);

    return ok;
  }

  Method FindMain() {
    ArrayList methods = new ArrayList();
    foreach (Class c in classes_) {
      c.GetMainMethod(methods);
    }
    if (methods.Count == 0) {
      Console.WriteLine("no Main() method found");
      return null;
    }
    if (methods.Count > 1) {
      Console.WriteLine("error: found more than one Main() method");
      return null;
    }
    return (Method) methods[0];
  }

  public void Eval(ArrayList /* of string */ args) {
    Method m = FindMain();
    if (m == null)
      return;
    ArrayList a = new ArrayList();
    if (m.parameters_.Count > 0) {   // Main() takes a string[] argument
      GArray arr = new GArray(new ArrayType(GString.type_), args.Count);
      for (int i = 0 ; i < args.Count ; ++i)
        arr.Set(i, new GString((string) args[i]));
      a.Add(arr);
    }
    foreach (Class c in classes_)
      c.StaticInit();
    m.Invoke(null, a);
  }

  void EmitMain(SourceWriter w, Method main) {
    bool args = main.parameters_.Count > 0;
    w.Write("int main({0}) ", args ? "int argc, char *argv[]" : "");
    w.OpenBrace();
    w.IWriteLine("return gel_runmain({0}::Main{1});", main.GetClass().name_, args ? ", argc, argv" : "");
    w.CloseBrace();
  }

  public bool Emit(SourceWriter w) {
    Method main = FindMain();
    if (main == null)
      return false;

    w.WriteLine("#define MEMORY_OWN 1");
    if (safe_)
      w.WriteLine("#define MEMORY_SAFE 1");
    if (crt_alloc_)
      w.WriteLine("#define MEMORY_CRT 1");
    if (profile_ref_)
      w.WriteLine("#define PROFILE_REF_OPS 1");

    foreach (string f in cpp_import_)
      w.WriteLine("#include {0}", GString.EmitString(f));

    // We undefine NULL since GEL2 code may legitimately define fields or variables with that name.
    // Generated code uses 0, not NULL, to indicate the null pointer.
    w.WriteLine("#undef NULL");

    w.WriteLine("");

    // Forward-declare all classes since C++ requires a class to be declared before its name can
    // be used as a type.
    foreach (Class c in classes_)
      if (!(c.HasAttribute(Attribute.Extern)))
        w.WriteLine("class {0};", c.name_);
    w.WriteLine("");

    foreach (Class c in classes_)
      c.EmitDeclaration(w);

    foreach (Class c in classes_)
      c.Emit(w);

    EmitMain(w, main);
    return true;
  }

  // Emit C++ code.
  public bool Generate(string basename) {
    string cpp_file = basename + ".cpp";
    StreamWriter w = new StreamWriter(cpp_file);
    bool ok = Emit(new SourceWriter(w));
    w.Close();
    return ok;
  }

  static string[] vsdirs = { "Microsoft Visual Studio 8",
                            "Microsoft Visual Studio .NET 2003",
                            "Microsoft Visual Studio .NET 2002" };

  string VsVars(int i) {
    return String.Format("C:\\Program Files\\{0}\\Common7\\Tools\\vsvars32.bat", vsdirs[i]);
  }

  string FindVsVars() {
    for (int i = 0; i < vsdirs.Length; ++i) {
      string f = VsVars(i);
      if (File.Exists(f))
        return f;
    }

    Console.WriteLine("Error: unable to find vsvars32.bat at any of the following locations:");
    Console.WriteLine("");
    for (int i = 0; i < vsdirs.Length; ++i)
      Console.WriteLine(VsVars(i));
    Console.WriteLine("");
    Console.WriteLine("Your Visual Studio installation may be invalid.  Aborting compilation.");
    return null;
  }

  int Run(string command, string args) {
    ProcessStartInfo i = new ProcessStartInfo(command, args);
    i.UseShellExecute = false;
    Process p = Process.Start(i);
    p.WaitForExit();
    return p.ExitCode;
  }

  // Report compilation error. Print error message from a file
  void ReportCompilationError(string error_msg_file) {
      Console.WriteLine(
        "Error: unable to compile generated C++ code.  You may have found a bug in the GEL2 compiler.");
      Console.WriteLine("C++ compiler output follows:");
      Console.WriteLine("");
      Console.WriteLine((new StreamReader(error_msg_file)).ReadToEnd());
  }

  int RunShellCommand(string command) {
    command = String.Format("-c \"{0}\"", command);
    return Run("/bin/sh", command);
  }

  void UnixCppCompile(string basename) {
    string command_out = Path.GetTempFileName();
    string dbg_options = debug_ ? "-g -DDEBUG" : "-O2 -DNDEBUG";
    StringBuilder sb = new StringBuilder();
    // -o basename, use basename as the executable name
    // -Werror, make all warnings into hard errors
    sb.AppendFormat("/usr/bin/g++ -o {0} -Werror {1} {2}.cpp",
                    basename, dbg_options, basename);

    // Append redirection of output
    sb.AppendFormat(" > {0} 2>&1", command_out);
 
    string sh_cmd = sb.ToString();

    if (Gel.verbose_) 
      Console.WriteLine(sh_cmd);
  
    // use /bin/sh as command to ru
    if (RunShellCommand(sh_cmd) !=0) {
      ReportCompilationError(command_out);
    }
    // delete intermediate files
    File.Delete(command_out);
    File.Delete(basename + ".o");
  }

  // Run vsvars32.bat and the run the given command, redirecting output to a file.
  // We must always redirect output since vsvars32 prints a message "Setting environment..."
  // which we don't want users to see.
  int VsRun(string vsvars, string command, string output) {
    command = String.Format("/c (\"{0}\" & {1}) > \"{2}\"", vsvars, command, output);
    return Run("cmd", command);
  }

  void VsCppCompile(string basename) {
    string vsvars = FindVsVars();
    if (vsvars == null)
      return;
    string command_out = Path.GetTempFileName();

    string options = debug_ ?
      //  /MDd - runtime library: multithreaded debug DLL
      //  /Od - optimization: disabled
      //  /ZI - debug information format: Program Database for Edit & Continue
      "/MDd /Od /ZI /D \"_DEBUG\"" :

      //  /MD - runtime library: multithreaded DLL
      //  /O2 - optimization: Maximize Speed
      //  /GL - optimization: Whole Program Optimization
      "/MD /O2 /GL /D \"NDEBUG\"";

    StringBuilder sb = new StringBuilder();
    //  /WX - treat warnings as errors
    sb.AppendFormat("cl /nologo /WX {0} {1}.cpp", options, basename);
    string mode = debug_ ? "debug" : "release";
    if (!crt_alloc_)
      sb.AppendFormat(" {0}\\{1}\\dlmalloc.obj", Gel.gel_directory_, mode);
    sb.Append(" user32.lib shell32.lib shlwapi.lib");
    string command = sb.ToString();
    if (Gel.verbose_)
      Console.WriteLine(command);

    if (VsRun(vsvars, command, command_out) != 0) {
      ReportCompilationError(command_out);
    } else {
      // Run the manifest tool to embed the linker-generated manifest into the executable file;
      // this allows the executable to find the C runtime DLL even when the manifest file is absent.
      string mt_command = String.Format("mt -nologo -manifest {0}.exe.manifest -outputresource:\"{0}.exe;#1\"",
                              basename);
      if (Gel.verbose_)
        Console.WriteLine(mt_command);
      if (VsRun(vsvars, mt_command, command_out) != 0)
        Console.WriteLine("warning: could not run manifest tool");
    }

    // delete intermediate files
    File.Delete(command_out);
    File.Delete(basename + ".obj");
    File.Delete(basename + ".exe.manifest");
  }

  // Invoke the C++ compiler to generate a native executable.
  void CppCompile(string basename) {
    if (Environment.OSVersion.Platform == PlatformID.Win32NT) 
      VsCppCompile(basename);
    else
      UnixCppCompile(basename);
  }

  public void Compile(string output, bool cpp_only) {
    if (output == null)
      output = Path.GetFileNameWithoutExtension((string) gel_import_[0]);
    if (Generate(output) && !cpp_only)
      CppCompile(output);
  }
}

class Scanner : Parser.yyInput {
  public readonly string filename_;

  int line_;
  public int Line() { return line_; }

  StreamReader sr_;
  int token_;
  object value_;

  int next_token_ = -1;
  object next_value_;

  public Scanner (string filename) {
    filename_ = filename;
    sr_ = new StreamReader(filename);
    line_ = 1;
  }

  public int Token {
    get {
      return token_;
    }
  }

  public object Value {
    get {
      return value_;
    }
  }

  int Read() {
    int i = sr_.Read();
    if (i == '\n')
      ++line_;
    return i;
  }

  bool Read(out char c) {
    int i = Read();
    c = (char) i;
    return i != -1;
  }

  char Peek() {
    return (char) sr_.Peek();
  }

  string ReadWord(char first) {
    StringBuilder sb = new StringBuilder();
    if (first != '\0')
      sb.Append(first);
    while (true) {
      char c = Peek();
      if (!Char.IsLetter(c) && !Char.IsDigit(c) && c != '_')
        break;
      sb.Append(c);
      Read();
    }
    return sb.ToString();
  }

  // Read to the end of the current line.
  string ReadLine() {
    StringBuilder sb = new StringBuilder();
    char c;
    while (Read(out c)) {
      if (c == '\n')
        break;
      sb.Append(c);
    }
    return sb.ToString();
  }

  // Read a comment delimited by /* ... */, assuming we've already read the opening /*.
  // Returns true on success, or false on EOF.
  bool ReadDelimitedComment() {
    char prev = (char) 0;
    while (true) {
      char c;
      if (!Read(out c)) {
        Console.WriteLine("warning: unterminated comment at end of file");
        return false;
      }
      if (c == '/' && prev == '*')
        return true;
      prev = c;
    }
  }

  // Preprocess input, handling whitespace, comments and directives; return the first
  // character not eaten by preprocessing, or -1 on EOF.
  int GetChar() {
    while (true) {
      int i = Read();
      if (i == -1)
        return -1;   // end of input
      if (i == 0)
        continue;   // end of this file; move on
      char c = (char) i;

      if (c == '\n' && Peek() == '#') {
        Read();
        string directive = ReadWord('\0');
        if (directive == "line") {
          ReadLine();
          continue;
        }
        Console.WriteLine("error: unknown preprocessing directive {0}", directive);
        Environment.Exit(0);
      }

      if (Char.IsWhiteSpace(c))
        continue;

      if (c == '/') {
        switch (Peek()) {
          case '/':  // single-line comment
            Read();
            int line = line_;
            string s = ReadLine();
            if (Gel.error_test_ && s.StartsWith(" error"))
              Gel.expected_error_lines_.Add(line);
            continue;

          case '*':  // comment delimited by /* ... */
            Read();   // move past opening '*'
            if (!ReadDelimitedComment())
              return (char) 0;
            continue;
        }
      }

      return c;
    }
  }

  bool IsDigit(char c, bool hex) {
    if (Char.IsDigit(c))
      return true;
    if (!hex)
      return false;
    return (c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F');
  }

  // Read a number, possibly including a decimal point and/or exponent.
  string ReadNumber(char first, bool hex, out bool real) {
    bool dot = false, exp = false;
    StringBuilder sb = new StringBuilder();
    sb.Append(first);
    if (first == '.')
      dot = true;

    while (true) {
      bool need_digit = false;
      char c = Peek();
      if (c == '.' && !hex && !dot && !exp) {
        sb.Append(c);
        Read();
        dot = true;
        c = Peek();
        need_digit = true;
      }
      else if ((c == 'e' || c == 'E') && !hex && !exp) {
        sb.Append(c);
        Read();
        exp = true;
        c = Peek();
        if (c == '+' || c == '-') {
          sb.Append(c);
          Read();
          c = Peek();
        }
        need_digit = true;
      }
      if (!IsDigit(c, hex)) {
        if (!need_digit)
          break;
        real = false;
        return null;
      }
      sb.Append(c);
      Read();
    }

    real = dot || exp;
    return sb.ToString();
  }

  int ParseNumber(char first, out object val) {
    bool hex = false;
    if (first == '0') {
      char p = Peek();
      if (p == 'x' || p == 'X') {
        Read();
        hex = true;
      }
    }
    bool real;
    string s = ReadNumber(first, hex, out real);
    if (s == null) {
      val = null;
      return Parser.SCAN_ERROR;
    }
    if (!hex) {
      switch (Peek()) {
        case 'F':
        case 'f':
          Read();
          val = Single.Parse(s);
          return Parser.FLOAT_LITERAL;
        case 'D':
        case 'd':
          Read();
          real = true;
        break;
    }
    }
    if (real) {
      val = Double.Parse(s);
      return Parser.DOUBLE_LITERAL;
    }
    val = int.Parse(s, hex ? NumberStyles.HexNumber : NumberStyles.Integer);
    return Parser.INT_LITERAL;
  }

  int ParseWord(char first, out object val) {
    string s = ReadWord(first);

    val = null;
    switch (s) {
      case "abstract": return Parser.ABSTRACT;
      case "as": return Parser.AS;
      case "base": return Parser.BASE;
      case "bool": return Parser.BOOL;
      case "break": return Parser.BREAK;
      case "case": return Parser.CASE;
      case "char": return Parser.CHAR;
      case "class": return Parser.CLASS;
      case "const": return Parser.CONST_TOKEN;
      case "continue": return Parser.CONTINUE;
      case "default": return Parser.DEFAULT;
      case "do": return Parser.DO;
      case "double": return Parser.DOUBLE;
      case "else": return Parser.ELSE;
      case "extern": return Parser.EXTERN;
      case "false": return Parser.FALSE_TOKEN;
      case "float": return Parser.FLOAT;
      case "for": return Parser.FOR;
      case "foreach": return Parser.FOREACH;
      case "if": return Parser.IF;
      case "import": return Parser.IMPORT;
      case "in": return Parser.IN_TOKEN;
      case "int": return Parser.INT;
      case "is": return Parser.IS;
      case "new": return Parser.NEW;
      case "null": return Parser.NULL;
      case "object": return Parser.OBJECT;
      case "out": return Parser.OUT_TOKEN;
      case "override": return Parser.OVERRIDE;
      case "pool": return Parser.POOL;
      case "private": return Parser.PRIVATE;
      case "protected": return Parser.PROTECTED;
      case "public": return Parser.PUBLIC;
      case "readonly": return Parser.READONLY;
      case "ref": return Parser.REF;
      case "return": return Parser.RETURN;
      case "short": return Parser.SHORT;
      case "static": return Parser.STATIC;
      case "string": return Parser.STRING;
      case "switch": return Parser.SWITCH;
      case "take": return Parser.TAKE;
      case "this": return Parser.THIS_TOKEN;
      case "true": return Parser.TRUE_TOKEN;
      case "virtual": return Parser.VIRTUAL;
      case "void": return Parser.VOID_TOKEN;
      case "while": return Parser.WHILE;
      default:
        val = s;
        return Parser.ID;
    }
  }

  bool ParseEscape(ref char c) {
    if (!Read(out c))
      return false;
    switch (c) {
      case '\'':
      case '\"':
      case '\\':
        break;
      case '0': c = '\0'; break;
      case 'n': c = '\n'; break;
      case 'r': c = '\r'; break;
      case 't': c = '\t'; break;
      default: return false;
    }
    return true;
  }

  int ParseChar(out object val) {
    val = null;
    char c;
    if (!Read(out c))
      return Parser.SCAN_ERROR;
    if (c == '\\' && !ParseEscape(ref c))
        return Parser.SCAN_ERROR;
    char d;
    if (!Read(out d) || d != '\'')
      return Parser.SCAN_ERROR;
    val = c;
    return Parser.CHAR_LITERAL;
  }

  int ParseString(out object val) {
    val = null;
    StringBuilder sb = new StringBuilder();
    while (true) {
      char c;
      if (!Read(out c))
        return Parser.SCAN_ERROR;
      if (c == '"')
        break;
      if (c == '\\' && !ParseEscape(ref c))
        return Parser.SCAN_ERROR;
      sb.Append(c);
    }
    val = sb.ToString();
    return Parser.STRING_LITERAL;
  }

  int ReadToken(out object val) {
    val = null;

    int i = GetChar();
    if (i == -1)
      return -1;
    char c = (char) i;

    if (Char.IsDigit(c) || c == '.' && Char.IsDigit(Peek()))
      return ParseNumber(c, out val);

    if (Char.IsLetter(c) || c == '_')
      return ParseWord(c, out val);

    if (c == '\'')
      return ParseChar(out val);

    if (c == '"')
      return ParseString(out val);

    char peek = Peek();
    int token = 0;
    switch (c.ToString() + peek.ToString()) {
      case "++": token = Parser.PLUS_PLUS; break;
      case "--": token = Parser.MINUS_MINUS; break;
      case "&&": token = Parser.OP_AND; break;
      case "||": token = Parser.OP_OR; break;
      case "==": token = Parser.OP_EQUAL; break;
      case "!=": token = Parser.OP_NE; break;
      case "<=": token = Parser.OP_LE; break;
      case "<<": token = Parser.OP_LEFT_SHIFT; break;
      case ">=": token = Parser.OP_GE; break;
      case ">>": token = Parser.OP_RIGHT_SHIFT; break;
      case "*=": token = Parser.STAR_EQUAL; break;
      case "/=": token = Parser.SLASH_EQUAL; break;
      case "%=": token = Parser.PERCENT_EQUAL; break;
      case "+=": token = Parser.PLUS_EQUAL; break;
      case "-=": token = Parser.MINUS_EQUAL; break;
      case "&=": token = Parser.AND_EQUAL; break;
      case "|=": token = Parser.OR_EQUAL; break;

      // Return [] as a single token; this lets the parser distinguish the cases
      // "foo[] a;" and "foo[x] = 4" as soon as it reads the first token after the identifier "foo".
      case "[]": token = Parser.ARRAY_TYPE; break;
    }
    if (token != 0) {
      Read();
    } else token = c;

    val = token;
    return token;
  }

  public bool Advance () {
    if (next_token_ != -1) {
      token_ = next_token_;
      value_ = next_value_;
      next_token_ = -1;
    } else {
      token_ = ReadToken(out value_);
      if (token_ == -1) return false;
    }

    if (token_ == ')') {
      // We need to read one token ahead to determine whether this close parenthesis
      // ends a type cast.  See e.g. the discussion in the Cast Expressions section
      // of the C# specification.
      next_token_ = ReadToken(out next_value_);
      switch (next_token_) {
        case -1:  // end of file
          break;
        case '!':
        case '(':
        case Parser.ID:
        case Parser.INT_LITERAL:
        case Parser.CHAR_LITERAL:
        case Parser.STRING_LITERAL:
          token_ = Parser.CAST_CLOSE_PAREN;
          break;
        default:
          if (next_token_ >= Parser.FIRST_KEYWORD && next_token_ <= Parser.LAST_KEYWORD &&
              next_token_ != Parser.AS && next_token_ != Parser.IS)
            token_ = Parser.CAST_CLOSE_PAREN;
          break;
      }
    }
    return true;
  }
}

class Gel {
  public static bool verbose_;

  public static bool error_test_;
  public static bool print_type_sets_;
  
  public static bool always_ref_;

  public static readonly ArrayList /* of int */ expected_error_lines_ = new ArrayList();
  public static readonly ArrayList /* of int */ error_lines_ = new ArrayList();

  public static Program program_;

  public static readonly string gel_directory_ = GetLibraryPath();

  static string GetLibraryPath() {
    return Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName); 
  }

  public static string CurrentFile() {
    return program_ == null ? "" : program_.CurrentFile();
  }

  public static int Line() {
    return program_ == null ? 0 : program_.Line();
  }

  public static void Exit() { Environment.Exit(0); }

  // Given two sorted ArrayLists a, b of ints, return an ArrayList containing all ints which appear
  // in [a] but not in [b].
  ArrayList Diff(ArrayList a, ArrayList b) {
    ArrayList ret = new ArrayList();
    int bi = 0;
    foreach (int i in a) {
      while (bi < b.Count && (int) b[bi] < i)
        ++bi;
      if (bi >= b.Count || (int) b[bi] > i)
        ret.Add(i);
    }
    return ret;
  }

  void WriteLineNumbers(ArrayList a) {
    for (int i = 0; i < a.Count; ++i) {
      if (i > 0)
        Console.Write(", ");
      Console.Write(a[i]);
    }
    Console.WriteLine("");
  }

  void ErrorTestReport() {
    Console.WriteLine("");
    ArrayList e = Diff(error_lines_, expected_error_lines_);
    if (e.Count > 0) {
      Console.Write("unexpected error at line ");
      WriteLineNumbers(e);
    }
    ArrayList e2 = Diff(expected_error_lines_, error_lines_);
    if (e2.Count > 0) {
      Console.Write("expected error at line ");
      WriteLineNumbers(e2);
    }
    if (e.Count == 0 && e2.Count == 0)
      Console.WriteLine("all errors expected");
  }

  void Usage() {
    Console.WriteLine("usage: gel <source-file> ... [args]");
    Console.WriteLine("       gel -c [-d] [-o <name>] [-u] [-v] [-cpp] <source-file> ...");
    Console.WriteLine("");
    Console.WriteLine("   -c: compile to native executable");
    Console.WriteLine("   -d: debug mode: disable optimizations, link with debug build of C runtime");
    Console.WriteLine("   -o: specify output filename");
    Console.WriteLine("   -u: unsafe: skip reference count checks");
    Console.WriteLine("   -v: verbose: display command used to invoke C++ compiler");
    Console.WriteLine(" -cpp: compile to C++ only");
  }

  public void Run(string[] args) {
    if (args.Length < 1) {
      Usage();
      return;
    }

    Internal.Init();

    program_ = new Program();
    bool compile = false;
    bool cpp_only = false;
    string output = null;
    int i;
    for (i = 0; i < args.Length && args[i].StartsWith("-"); ++i)
      switch (args[i]) {
        case "-c": compile = true; break;
        case "-d": program_.debug_ = true; break;
        case "-e": error_test_ = true; break;
        case "-o":
          if (++i >= args.Length) {
            Usage();
            return;
          }
          output = Path.GetFileNameWithoutExtension(args[i]);
          break;
        case "-p": program_.profile_ref_ = true; break;
	case "-r": always_ref_ = true; break;
        case "-u": program_.safe_ = false; break;
        case "-v": verbose_ = true; break;
        case "-cpp": cpp_only = true; break;
        case "-crt": program_.crt_alloc_ = true; break;
        case "-typeset": print_type_sets_ = true; break;
        default:
          Console.WriteLine("unrecognized option: {0}", args[i]);
          return;
      }

    for (; i < args.Length; ++i)
      if (args[i].EndsWith(".gel"))
        program_.Import(args[i]);
      else {
        if (args[i] == "-")   // marker indicating end of source files
          ++i;
        break;
      }

    ArrayList program_args = new ArrayList();
    if (compile) {
      if (i < args.Length) {
        Console.WriteLine("file to compile has unrecognized extension: {0}", args[i]);
        return;
      }
    } else {
      for (; i < args.Length; ++i)
        program_args.Add(args[i]);
    }

    if (!program_.Parse())
      return;

    bool ok = program_.Resolve() && program_.Check();
    if (error_test_)
      ErrorTestReport();
    if (!ok)
      return;

    if (compile)
      program_.Compile(output, cpp_only);
    else program_.Eval(program_args);
  }

  public static void Main(string[] args) {
    new Gel().Run(args);
  }
}
