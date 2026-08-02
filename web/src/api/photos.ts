/**
 * Getting a picture of a zone into a shape worth sending.
 *
 * A photo straight off a phone is eight to twelve megabytes of detail that this
 * app has nowhere to put: it is shown in a card a few hundred pixels across. All
 * that resolution costs upload time on whatever connection someone is standing in
 * the garden with, and disk on the machine at home for ever after.
 */

/** The picture formats the server stores, and the extension it expects for each. */
export const PHOTO_EXTENSIONS: Readonly<Record<string, string>> = {
  'image/jpeg': '.jpg',
  'image/png': '.png',
  'image/webp': '.webp',
};

/**
 * Longest edge worth keeping.
 *
 * The photo is displayed about 190px tall and the width of a sheet. This leaves
 * enough for a high-density screen and a generous crop, and nothing beyond what
 * any screen in the app can show.
 */
const MAX_EDGE = 1600;

/**
 * Under this, leave the file alone.
 *
 * Re-encoding a small photo spends quality to save bytes that were never the
 * problem, and a file this size uploads quickly regardless.
 */
const LEAVE_ALONE_BYTES = 600 * 1024;

const OUTPUT_TYPE = 'image/jpeg';
const OUTPUT_QUALITY = 0.85;

/**
 * A filename the server will accept for an uploaded photo.
 *
 * Falls back to whatever the picker supplied when the type is unrecognised, so the
 * server gets to give its own answer about what it does and does not take rather
 * than this quietly inventing an extension for it.
 */
export function uploadNameFor(file: File): string {
  const extension = PHOTO_EXTENSIONS[file.type];
  return extension ? `photo${extension}` : file.name;
}

/**
 * Scales a photo down to something sensible, or returns it untouched.
 *
 * Every failure path returns the original. A large photo that uploads is worth
 * more than a small one that does not, so nothing here is allowed to be the
 * reason a picture fails to save.
 *
 * Re-encoding also drops the EXIF block, which on a phone photo carries the
 * coordinates of the place it was taken — in this case, the user's garden.
 * Nothing in this app wants that, and it would otherwise sit on disk indefinitely.
 */
export async function shrinkForUpload(file: File): Promise<File> {
  if (file.size <= LEAVE_ALONE_BYTES) return file;

  let bitmap: ImageBitmap;
  try {
    // "from-image" is what applies the EXIF orientation flag. Without it a photo
    // taken in portrait decodes on its side, and the sideways version is what
    // would be saved. If this is unavailable the original goes up unchanged: a
    // correctly-oriented large photo beats a small one lying on its side.
    bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' });
  } catch {
    return file;
  }

  try {
    const scale = Math.min(1, MAX_EDGE / Math.max(bitmap.width, bitmap.height));

    // Big in bytes but not in pixels — already a JPEG at this size, so re-encoding
    // would only cost quality.
    if (scale === 1 && file.type === OUTPUT_TYPE) return file;

    const width = Math.max(1, Math.round(bitmap.width * scale));
    const height = Math.max(1, Math.round(bitmap.height * scale));

    const canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;

    const context = canvas.getContext('2d');
    if (!context) return file;

    context.drawImage(bitmap, 0, 0, width, height);

    const blob = await new Promise<Blob | null>((resolve) =>
      canvas.toBlob(resolve, OUTPUT_TYPE, OUTPUT_QUALITY),
    );

    // A picture that compresses badly — a screenshot, mostly flat colour — can come
    // out of this larger than it went in. Keep whichever is smaller.
    if (!blob || blob.size >= file.size) return file;

    return new File([blob], `photo${PHOTO_EXTENSIONS[OUTPUT_TYPE]}`, { type: OUTPUT_TYPE });
  } catch {
    return file;
  } finally {
    bitmap.close();
  }
}
