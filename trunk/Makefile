all: jay/src/jay gel2a

jay/src/jay:
	make -C jay/src jay

gel2.tab.cs: gel2_cs.jay
	jay/src/jay -cv gel2_cs.jay < jay/cs/skeleton.cs > gel2.tab.cs

gel2.exe: gel2.cs gel2.tab.cs
	mcs gel2.cs gel2.tab.cs

gel2.tab.gel2: gel2.jay
	jay/src/jay -cov gel2.jay < jay/gel2/skeleton.gel2 > gel2.tab.gel2

gel2a: gel2.exe gel2.gel2 gel2.tab.gel2
	mono gel2.exe -c -d -o gel2a gel2.gel2

gel2b: gel2a gel2.gel2 gel2.tab.c
	./gel2a -c -d -o gel2b gel2.gel2

helloworld: helloworld.gel2
	mono gel2.exe -c -d -o helloworld helloworld.gel2

clean:
	rm -f *~ gel2.exe gel2.tab.gel2 gel2.tab.cs gel2a gel2a.cpp gel2b gel2b.cpp helloworld.cpp y.output
