CPPFLAGS = -Wall -O2

all: binarytrees-cpp binarytrees.class binarytrees-cs.exe binarytrees-gel \
     nsieve-cpp nsieve.class nsieve-cs.exe nsieve-gel \
     sort-cpp sort.class sort-cs.exe sort-gel \
     sortstring-cpp sortstring.class sortstring-cs.exe sortstring-gel

%.class: %.java
	javac $<

%.exe: %.cs
	mcs $<

%: %.gel
	../gel.exe -c -v $<

clean:
	rm -f *~ *-cpp *.class *.exe *-gel *-gel.cpp
