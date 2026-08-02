/**
 * Getting a picture of a zone into a shape worth sending.
 *
 * A photo straight off a phone is eight to twelve megabytes of detail that this
 * app has nowhere to put, and the cost is not only the upload. The zone list draws
 * every photo at 52 pixels square; handing it the original meant a phone decoding
 * several megapixels per zone, all at once, to fill squares smaller than a
 * fingernail — which is what made the list stutter as it animated in.
 *
 * So two renditions come out of one decode: the photo, sized for the largest place
 * it is shown, and a thumbnail for the list.
 */

/** The picture formats the server stores, and the extension it expects for each. */
export const PHOTO_EXTENSIONS: Readonly<Record<string, string>> = {
  'image/jpeg': '.jpg',
  'image/png': '.png',
  'image/webp': '.webp',
};

/**
 * Longest edge of the photo itself.
 *
 * The biggest it is ever shown is the zone sheet, a little over 500 points wide and
 * 190 tall. This covers that on a dense screen with room to spare, and is well
 * under what a camera produces.
 */
const PHOTO_EDGE = 1280;

/**
 * Longest edge of the thumbnail: the 52-point square the list draws, with enough
 * left over for a three-times display.
 */
const THUMB_EDGE = 192;

const OUTPUT_TYPE = 'image/jpeg';
const PHOTO_QUALITY = 0.82;
const THUMB_QUALITY = 0.72;

/** What actually gets uploaded: the photo, and a thumbnail when one could be made. */
export type PreparedPhoto = { photo: File; thumb: File | null };

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
 * Scales a photo down and cuts a thumbnail from it.
 *
 * Every failure path returns the original photo with no thumbnail. A large photo
 * that saves is worth more than a small one that does not, so nothing here is
 * allowed to be the reason a picture fails to upload — and the list falls back to
 * the full photo when a thumbnail is missing.
 *
 * Re-encoding also drops the EXIF block, which on a phone photo carries the
 * coordinates of the place it was taken — in this case, the user's garden. Nothing
 * in this app wants that, and it would otherwise sit on disk indefinitely.
 */
export async function prepareForUpload(file: File): Promise<PreparedPhoto> {
  let bitmap: ImageBitmap;
  try {
    // "from-image" is what applies the EXIF orientation flag. Without it a photo
    // taken in portrait decodes on its side, and the sideways version is what
    // would be saved. If this is unavailable the original goes up unchanged: a
    // correctly-oriented large photo beats a small one lying on its side.
    bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' });
  } catch {
    return { photo: file, thumb: null };
  }

  try {
    const rendered = await render(bitmap, PHOTO_EDGE, PHOTO_QUALITY);

    // Whichever is smaller. Re-encoding something mostly flat — a screenshot, a
    // diagram — produces a larger file than it started with, and there is no sense
    // in spending detail to end up with more bytes.
    const photo = rendered && rendered.size < file.size ? rendered : file;

    return { photo, thumb: await render(bitmap, THUMB_EDGE, THUMB_QUALITY) };
  } catch {
    return { photo: file, thumb: null };
  } finally {
    bitmap.close();
  }
}

/** Draws the decoded image at a bounded size and encodes it. */
async function render(bitmap: ImageBitmap, maxEdge: number, quality: number): Promise<File | null> {
  const scale = Math.min(1, maxEdge / Math.max(bitmap.width, bitmap.height));
  const width = Math.max(1, Math.round(bitmap.width * scale));
  const height = Math.max(1, Math.round(bitmap.height * scale));

  const canvas = document.createElement('canvas');
  canvas.width = width;
  canvas.height = height;

  const context = canvas.getContext('2d');
  if (!context) return null;

  context.drawImage(bitmap, 0, 0, width, height);

  const blob = await new Promise<Blob | null>((resolve) =>
    canvas.toBlob(resolve, OUTPUT_TYPE, quality),
  );

  return blob
    ? new File([blob], `photo${PHOTO_EXTENSIONS[OUTPUT_TYPE]}`, { type: OUTPUT_TYPE })
    : null;
}
