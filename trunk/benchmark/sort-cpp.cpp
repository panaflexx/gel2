#include <assert.h>
#include <stdio.h>
#include <stdlib.h>

class Random {
  static int r_;

 public:
  static int Next() {
    r_ = r_ * 69069;
    return r_;
  }
};

/* static */ int Random::r_ = 1;

class Node {
public:
  int i_;
  Node *next_;

  Node(int i, Node *next) { i_ = i; next_ = next; }
};

class Sort {
  static Node *RandomList(int count) {
    Node *first = NULL;
    for (int x = 0 ; x < count ; ++x)
      first = new Node(Random::Next(), first);
    return first;
  }

  static Node *Merge(Node *a, Node *b) {
    Node *head = NULL, *tail = NULL;
    while (a != NULL && b != NULL) {
      Node *top;
      if (a->i_ < b->i_) {
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
      Node *n = RandomList(1000000);
      puts(" sorting...");
      n = MergeSort(n);

      puts(" deleting...");
      while (n != NULL) {
        Node *next = n->next_;
        if (next != NULL && n->i_ > next->i_) {
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
