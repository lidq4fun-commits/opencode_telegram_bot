// Type declarations for the fork's JavaScript EOT10 protocol module.
// The implementation lives in EOT10Crypto.mjs next to package.json and is
// shipped with the desktop installer without TypeScript sources.
declare module "*EOT10Crypto.mjs" {
  export interface Eot10MediaMetadata {
    filename: string;
    mimeType: string;
    kind?: string;
  }

  export function encryptEot10(plaintext: string): string;
  export function decryptEot10(value: string): string;
  export function isEot10Message(value: unknown): boolean;
  export function encryptEot10RichMessage(richMessage: unknown): unknown;
  export function encryptEot10MediaInput(
    input: unknown,
  ): Promise<{ buffer: Buffer; filename: string }>;
  export function encryptEot10MediaBuffer(
    fileBytes: Buffer,
    metadata?: Partial<Eot10MediaMetadata>,
  ): Buffer;
  export function decryptEot10MediaBuffer(value: Buffer): {
    buffer: Buffer;
    metadata: Eot10MediaMetadata;
  };
  export function isEot10MediaBuffer(value: unknown): boolean;
  export function isEot10MediaDocument(document: unknown): boolean;
  export function cacheDecryptedEot10Media(
    fileId: string,
    media: { buffer: Buffer; filePath: string },
  ): void;
  export function takeDecryptedEot10Media(
    fileId: string,
  ): { buffer: Buffer; filePath: string } | undefined;
  export function encryptEot10Parts(
    plaintext: string,
    maxPlaintextChars?: number,
  ): string[];
}
