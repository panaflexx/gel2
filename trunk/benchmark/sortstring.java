class Random {
  static int r_ = 1;

  public static int Next() {
    r_ = r_ * 69069;
    return r_;
  }

  public static String NextString()
  {
    int len = ((Next() & 0xf00) >> 8) + 1;   // 1 to 16 characters
    char[] a = new char[len];
    for (int i = 0; i < len; ++i) {
      int j = ((Next() & 0x3f00) >> 8) + 32;
      a[i] = (char) j;
    }
    return new String(a);
  }
}

class Node {
  public final String s_;
  public Node next_;

  public Node(String s, Node next) { s_ = s; next_ = next; }
}

class sortstring {
  static Node RandomList(int count) {
    Node first = null;
    for (int x = 0 ; x < count ; ++x)
      first = new Node(Random.NextString(), first);
    return first;
  }

  static Node Merge(Node a, Node b) {
    Node head = null, tail = null;
    while (a != null && b != null) {
      Node top;
      if (a.s_.compareTo(b.s_) < 0) {
        top = a;
        a = top.next_;
      } else {
        top = b;
        b = top.next_;
      }
      top.next_ = null;
      if (head == null)
        head = tail = top;
      else {
        tail.next_ = top;
        tail = top;
      }
    }
    Node rest = (a == null) ? b : a;
    if (tail == null)
      return rest;
    tail.next_ = rest;
    return head;
  }

  static Node MergeSort(Node a) {
    if (a == null || a.next_ == null) return a;
    Node c = a;
    Node b = c.next_;
    while (b != null && b.next_ != null) {
      c = c.next_;
      b = b.next_.next_;
    }
    Node d = c.next_;
    c.next_ = null;
    return Merge(MergeSort(a), MergeSort(d));
  }

  public static void main(String[] args) {
    int iterations = args.length > 0 ? Integer.parseInt(args[0]) : 10;
    for (int iter = 1; iter <= iterations; ++iter) {
      System.out.println("iteration " + iter);
      Node n = RandomList(400000);
      n = MergeSort(n);

      while (n != null) {
        Node next = n.next_;
        if (next != null && n.s_.compareTo(next.s_) > 0) {
          System.out.println("failed");
          return;
        }
        n = next;
      }
    }
    System.out.println("succeeded");
  }
}
