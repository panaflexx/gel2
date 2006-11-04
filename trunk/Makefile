all: jay/src/jay gela

jay/src/jay:
	make -C jay/src jay

gel.tab.cs: gel_cs.jay
	jay/src/jay -cv gel_cs.jay < jay/cs/skeleton.cs > gel.tab.cs

gel.exe: gel.cs gel.tab.cs
	mcs gel.cs gel.tab.cs

gel.tab.gel: gel.jay
	jay/src/jay -cov gel.jay < jay/gel2/skeleton.gel > gel.tab.gel

gela: gel.exe gel.gel gel.tab.gel
	mono gel.exe -c -d -v -o gela gel.gel

gelb: gela gel.gel gel.tab.c
	./gela -c -d -v -o gelb gel.gel

helloworld: helloworld.gel
	mono gel.exe -c -d -v -o helloworld helloworld.gel

clean:
	rm -f *~ gel.exe gel.tab.gel gel.tab.cs gela gela.cpp gelb gelb.cpp helloworld.cpp y.output
