// Define a simple storage configuration service
export interface StorageConfig {
  storageType: string | undefined;
  getStorageType: () => string | undefined;
  setStorageType: (type: string | undefined) => void;
}

// Create a singleton instance
export const storageConfig: StorageConfig = {
  // Default to undefined (will fallback to local storage)
  storageType: undefined,

  getStorageType() {
    return this.storageType ?? "localstorage";
  },

  setStorageType(type: string | undefined) {
    this.storageType = type;
  },
};
