CPPFLAGS = -Wall -O2

all: binarytrees-cpp binarytrees.class binarytrees-cs.exe binarytrees-gel2 \
     nsieve-cpp nsieve.class nsieve-cs.exe nsieve-gel2 \
     sort-cpp sort.class sort-cs.exe sort-gel2 \
     sortstring-cpp sortstring.class sortstring-cs.exe sortstring-gel2

%.class: %.java
	javac $<

%.exe: %.cs
	mcs $<

%: %.gel2
	../gel2.exe -c -v $<

clean:
	rm -f *~ *-cpp *.class *.exe *-gel2 *-gel2.cpp
