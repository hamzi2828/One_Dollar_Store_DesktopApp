SecuGen fingerprint readers (Hamster Pro 20 / HU20 and the U20 family)

Put every DLL from the bin\x64 folder of SecuGen's free "FDx SDK Pro for
Windows" here (sgfplib.dll must be the 64-bit one), then run build.cmd.

Get the SDK: https://secugen.com/request-free-software  (pick "FDx SDK Pro for
Windows"; SecuGen emails the download link).

The DLLs are not committed (licence); see .gitignore.

Also here: vcruntime140.dll, vcruntime140_1.dll, msvcp140.dll (Microsoft VC++ 2015-2022 x64 runtime, app-local). sgwsqlib.dll and sgbledev.dll need them; without them a PC with no VC++ redistributable says 'sgfplib.dll not found'.
