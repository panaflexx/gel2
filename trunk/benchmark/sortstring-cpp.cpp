#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>

class Random {
  static int r_;

 public:
  static int Next() {
    r_ = r_ * 69069;
    return r_;
  }

  static wchar_t *NextString() {
    int len = ((Next() & 0xf00) >> 8) + 1;   // 1 to 16 characters
    wchar_t *s = new wchar_t[len + 1];
    for (int i = 0; i < len; ++i) {
      int j = ((Next() & 0x3f00) >> 8) + 32;
      s[i] = (wchar_t) j;
    }
    s[len] = '\0';
    return s;
  }
};

/* static */ int Random::r_ = 1;

class Node {
public:
  const wchar_t *s_;
  Node *next_;

  Node(const wchar_t *s, Node *next) { s_ = s; next_ = next; }
  
  ~Node() { delete [] s_; }
};

class Sort {
  static Node *RandomList(int count) {
    Node *first = NULL;
    for (int x = 0 ; x < count ; ++x)
      first = new Node(Random::NextString(), first);
    return first;
  }

  static Node *Merge(Node *a, Node *b) {
    Node *head = NULL, *tail = NULL;
    while (a != NULL && b != NULL) {
      Node *top;
      if (wcscmp(a->s_, b->s_) < 0) {
        top = a;
        a = top->next_;
      } else {
        top = b;
        b = top->next_;
      }
      top->next_ = NULL;
      if (head == NULL)
        head = tail = top;
      else {
        tail->next_ = top;
        tail = top;
      }
    }
    Node *rest = (a == NULL) ? b : a;
    if (tail == NULL)
      return rest;
    tail->next_ = rest;
    return head;
  }

  static Node *MergeSort(Node *a) {
    if (a == NULL || a->next_ == NULL) return a;
    Node *c = a;
    Node *b = c->next_;
    while (b != NULL && b->next_ != NULL) {
      c = c->next_;
      b = b->next_->next_;
    }
    Node *d = c->next_;
    c->next_ = NULL;
    return Merge(MergeSort(a), MergeSort(d));
  }

 public:
  static void Main(int iterations) {
    for (int iter = 1; iter <= iterations; ++iter) {
      printf("iteration %d\n", iter);
      Node *n = RandomList(400000);
      puts(" sorting...");
      n = MergeSort(n);

      puts(" deleting...");
      while (n != NULL) {
        Node *next = n->next_;
        if (next != NULL && wcscmp(n->s_, next->s_) > 0) {
          puts("failed");
          return;
        }
        delete n;
        n = next;
      }
    }
    puts("succeeded");
  }
};

int main(int argc, char *argv[]) {
  Sort::Main(argc >= 2 ? atoi(argv[1]) : 10);
  return 0;
}
