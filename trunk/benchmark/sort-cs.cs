using System;
using System.Diagnostics;

class Random {
  static int r_ = 1;

  public static int Next() {
    r_ = r_ * 69069;
    return r_;
  }
}

class Node {
  public readonly int i_;
  public Node next_;

  public Node(int i, Node next) { i_ = i; next_ = next; }
}

class Sort {
  static Node RandomList(int count) {
    Node first = null;
    for (int x = 0 ; x < count ; ++x)
      first = new Node(Random.Next(), first);
    return first;
  }

  static Node Merge(Node a, Node b) {
    Node head = null, tail = null;
    while (a != null && b != null) {
      Node top;
      if (a.i_ < b.i_) {
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

  public static void Main(String[] args) {
    int iterations = args.Length > 0 ? Int32.Parse(args[0]) : 10;
    for (int iter = 1; iter <= iterations; ++iter) {
      Console.WriteLine("iteration {0}", iter);
      Node n = RandomList(1000000);
      n = MergeSort(n);

      while (n != null) {
        Node next = n.next_;
        if (next != null && n.i_ > next.i_) {
          Console.WriteLine("failed");
          return;
        }
        n = next;
      }
    }
    Console.WriteLine("succeeded");
  }
}
