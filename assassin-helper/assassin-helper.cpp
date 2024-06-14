#include <iostream>
#include <fstream>
#include <vector>
#include <Windows.h>
#include <stdint.h>
#include <string.h>
#define MANIFEST_HEADER_SIZE 4

enum CompressedFileType {
	LZMS = 3,
	Delta = 4
};

typedef struct _LBLOB {
	size_t length;
	size_t fill;
	unsigned char* pData;

public:
	_LBLOB()
		: length(0), fill(0), pData(nullptr) {}
	_LBLOB(size_t length, unsigned char* pData)
		: length(length), fill(length), pData(pData) {}
} LBLOB;

namespace Windows::WCP::Rtl {
	typedef long(__stdcall* GetCompressedFileType)(struct _LBLOB const*);

	class AutoLZMSDecoder {
	public:
		typedef HRESULT Initialize();
	};

	class AutoLZMSEncoder {
	public:
		typedef HRESULT Initialize();
	};
}

namespace Windows {

}

namespace Windows::Rtl {
	class AutoDeltaBlob : public _LBLOB {

	};
}



typedef long(__stdcall* GetCompressedFileType)(struct _LBLOB const*);

typedef HRESULT(__stdcall* LoadFirstResourceLanguageAgnostic)(
	unsigned long,
	struct HINSTANCE__*,
	unsigned short const*,
	unsigned short const*,
	struct _LBLOB*
	);

typedef HRESULT (__stdcall *InitializeDeltaCompressor)(void*);
typedef HRESULT (__stdcall *DeltaDecompressBuffer)(
	unsigned long, struct _LBLOB*,
	unsigned long, struct _LBLOB*,
	class Windows::Rtl::AutoDeltaBlob*);


#ifdef BUILD_DLL
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __declspec(dllimport)
#endif

extern "C" {
	__declspec(dllexport) unsigned char * ParseFile(const char* filename)
	{
		std::ifstream ifs(filename, std::ios::binary);
		std::vector<unsigned char> buffer(std::istreambuf_iterator<char>(ifs), {});

		int ret;

		LBLOB inData = LBLOB(buffer.size(), buffer.data());

		HINSTANCE hWcp = LoadLibraryA("wcp.dll");

		GetCompressedFileType GCFT = (GetCompressedFileType)GetProcAddress(hWcp, "?GetCompressedFileType@Rtl@WCP@Windows@@YAKPEBU_LBLOB@@@Z");
		InitializeDeltaCompressor IDC = (InitializeDeltaCompressor)GetProcAddress(hWcp, "?InitializeDeltaCompressor@Rtl@Windows@@YAJPEAX@Z");
		//DeltaCompressFile = (void *)GetProcAddress(wcp, "?DeltaCompressFile@Rtl@Windows@@YAJKPEAUIRtlFile@12@PEBU_LBLOB@@00@Z");
		DeltaDecompressBuffer DDB = (DeltaDecompressBuffer)GetProcAddress(hWcp, "?DeltaDecompressBuffer@Rtl@Windows@@YAJKPEAU_LBLOB@@_K0PEAVAutoDeltaBlob@12@@Z");
		LoadFirstResourceLanguageAgnostic LFRLA = (LoadFirstResourceLanguageAgnostic)GetProcAddress(hWcp, "?LoadFirstResourceLanguageAgnostic@Rtl@Windows@@YAJKPEAUHINSTANCE__@@PEBG1PEAU_LBLOB@@@Z");

		unsigned int ftyp = GCFT(&inData);

		ret = IDC(nullptr);


		LBLOB dictionary;


		ret = LFRLA(
			0,
			hWcp,
			reinterpret_cast<const unsigned short*>(0x266),
			reinterpret_cast<const unsigned short*>(0x1),
			&dictionary
		);

		if (ftyp == CompressedFileType::LZMS) {
			throw "LZMS - TODO";
		}

		Windows::Rtl::AutoDeltaBlob outData;
		ret = DDB(
			2,
			&dictionary,
			MANIFEST_HEADER_SIZE,
			&inData,
			&outData
		);

		return outData.pData;
	}
}