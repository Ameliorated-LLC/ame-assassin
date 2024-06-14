#include <stdint.h>
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
	typedef long (__stdcall *GetCompressedFileType)(struct _LBLOB const*);

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