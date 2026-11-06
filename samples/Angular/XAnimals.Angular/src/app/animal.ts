/** An animal, as the server sends it (see XAnimals.Models.Animal). */
export interface Animal {
  name: string;
  location: string;
  details: string;
  thumbnailUrl: string;
  photoUrl: string;
}
